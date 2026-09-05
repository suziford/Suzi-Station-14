using System.Net;
using Content.Goobstation.Common.CCVar;
using Content.Goobstation.Shared.VoiceChat;
using Robust.Client.Audio;
using Robust.Client.Player;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Goobstation.Client.Voice;

/// <summary>
/// Client-side manager for voice chat functionality.
/// Handles network messages, manages voice streams, session auto-connect info, and speech events.
/// </summary>
public sealed class VoiceChatClientManager : IVoiceChatManager
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IAudioManager _audioManager = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly INetManager _netManager = default!;
    [Dependency] private readonly IClientNetManager _clientNetManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IResourceManager _resourceManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    private AudioSystem? _audioSystem = default!;

    private ISawmill _sawmill = default!;
    private readonly Dictionary<EntityUid, VoiceStreamManager> _activeStreams = new();

    public event Action<EntityUid, float>? OnEntitySpeaking;
    public event Action<bool, float>? OnLocalSpeaking;

    private int _sampleRate = 48000;
    private float _volume = 0.5f;
    private bool _hearSelf = false;
    private TimeSpan _lastLocalSpeechTime = TimeSpan.Zero;

    public void Initalize()
    {
        IoCManager.InjectDependencies(this);
        _sawmill = Logger.GetSawmill("voiceclient");

        _volume = _cfg.GetCVar(GoobCVars.VoiceChatVolume);
        _hearSelf = _cfg.GetCVar(GoobCVars.VoiceChatHearSelf);
        _sawmill.Info($"VoiceChatClientManager initialized with volume: {_volume}, hear_self: {_hearSelf}");
        _cfg.OnValueChanged(GoobCVars.VoiceChatVolume, OnVolumeChanged, true);
        _cfg.OnValueChanged(GoobCVars.VoiceChatHearSelf, OnHearSelfChanged, true);

        _clientNetManager.ClientConnectStateChanged += OnConnectStateChanged;
        _clientNetManager.Connected += OnConnected;
        _clientNetManager.Disconnect += OnDisconnected;

        _netManager.RegisterNetMessage<MsgVoiceChat>(OnVoiceMessageReceived);

        if (_clientNetManager.IsConnected)
        {
            UpdateSessionFile();
        }

        _sawmill.Info("VoiceChatClientManager initialized");
    }

    private void OnConnectStateChanged(ClientConnectionState state)
    {
        if (state == ClientConnectionState.Connected)
        {
            UpdateSessionFile();
        }
        else if (state == ClientConnectionState.NotConnecting)
        {
            ClearSessionFile();
        }
    }

    private void OnConnected(object? sender, NetChannelArgs e)
    {
        UpdateSessionFile();
    }

    private void OnDisconnected(object? sender, NetDisconnectedArgs e)
    {
        ClearSessionFile();
    }

    private string _lastWrittenSession = "";

    private void UpdateSessionFile()
    {
        try
        {
            var channel = _clientNetManager.ServerChannel;
            if (channel == null)
                return;

            var ip = channel.RemoteEndPoint.Address;
            string host;
            if (ip.IsIPv4MappedToIPv6)
                host = ip.MapToIPv4().ToString();
            else if (IPAddress.IsLoopback(ip))
                host = "127.0.0.1";
            else
                host = ip.ToString();

            var port = _cfg.GetCVar(GoobCVars.VoiceChatPort);
            if (port <= 0)
                port = 1213;

            var userId = _playerManager.LocalUser?.ToString() ?? "";
            var charName = _playerManager.LocalSession?.Name ?? "";

            var json = $"{{\n  \"connected\": true,\n  \"host\": \"{host}\",\n  \"port\": {port},\n  \"userId\": \"{userId}\",\n  \"characterName\": \"{charName}\"\n}}";
            if (json != _lastWrittenSession)
            {
                _lastWrittenSession = json;
                _resourceManager.UserData.WriteAllText(new ResPath("/voice_session.json"), json);
                _sawmill.Info($"Updated voice session file: {host}:{port} (userId={userId})");
            }
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Failed to update voice session file: {ex.Message}");
        }
    }

    private void ClearSessionFile()
    {
        try
        {
            var sessionPath = new ResPath("/voice_session.json");
            if (_resourceManager.UserData.Exists(sessionPath))
            {
                _resourceManager.UserData.WriteAllText(sessionPath, "{\n  \"connected\": false\n}");
            }
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Failed to clear voice session file: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle volume changes from CVars.
    /// </summary>
    private void OnVolumeChanged(float volume)
    {
        _volume = volume;

        foreach (var stream in _activeStreams.Values)
        {
            stream.SetVolume(_volume);
        }

        _sawmill.Debug($"Voice chat volume changed to {volume}");
    }

    /// <summary>
    /// Handle hear_self changes from CVars.
    /// </summary>
    private void OnHearSelfChanged(bool hearSelf)
    {
        _hearSelf = hearSelf;
        _sawmill.Debug($"Voice chat hear_self changed to {hearSelf}");
    }

    /// <summary>
    /// Handle incoming voice chat network messages.
    /// </summary>
    private void OnVoiceMessageReceived(MsgVoiceChat message)
    {
        if (message.PcmData == null || message.SourceEntity == null)
        {
            _sawmill.Warning("Received invalid voice chat message (null data or source)");
            return;
        }

        var sourceUid = _entityManager.GetEntity(message.SourceEntity.Value);
        if (!sourceUid.IsValid())
        {
            _sawmill.Warning($"Received voice chat message for invalid entity: {message.SourceEntity}");
            return;
        }

        AddPacket(sourceUid, message.PcmData);
    }

    /// <inheritdoc/>
    public void AddPacket(EntityUid sourceEntity, byte[] pcmData)
    {
        _audioSystem ??= _entityManager.System<AudioSystem>();

        // Calculate audio amplitude (RMS / peak)
        float maxLevel = 0f;
        for (int i = 0; i < pcmData.Length - 1; i += 2)
        {
            short sample = (short) (pcmData[i] | (pcmData[i + 1] << 8));
            float abs = Math.Abs(sample) / 32768f;
            if (abs > maxLevel)
                maxLevel = abs;
        }

        OnEntitySpeaking?.Invoke(sourceEntity, maxLevel);

        var localPlayer = _playerManager.LocalEntity;
        if (localPlayer == sourceEntity)
        {
            _lastLocalSpeechTime = _timing.CurTime;
            OnLocalSpeaking?.Invoke(true, maxLevel);

            if (!_hearSelf)
            {
                _sawmill.Debug($"[VOICE DEBUG] Filtering out audio playback from own entity {sourceEntity} (hear_self disabled)");
                return;
            }
        }

        if (!TryGetStreamManager(sourceEntity, out var streamManager))
        {
            _sawmill.Info($"[VOICE DEBUG] Creating new voice stream for entity {sourceEntity}");
            streamManager = new VoiceStreamManager(_audioManager, _audioSystem, sourceEntity, _sampleRate);
            streamManager.SetVolume(_volume);
            AddStreamManager(sourceEntity, streamManager);
        }
        else
        {
            _sawmill.Debug($"[VOICE DEBUG] Using existing voice stream for entity {sourceEntity}");
        }

        _sawmill.Debug($"[VOICE DEBUG] Adding packet to stream for entity {sourceEntity} (stream count: {_activeStreams.Count})");
        streamManager.AddPacket(pcmData);
    }

    /// <inheritdoc/>
    public bool TryGetStreamManager(EntityUid sourceEntity, out VoiceStreamManager streamManager)
    {
        if (_activeStreams.TryGetValue(sourceEntity, out var manager))
        {
            streamManager = manager;
            return true;
        }

        streamManager = null!;
        return false;
    }

    /// <inheritdoc/>
    public void AddStreamManager(EntityUid sourceEntity, VoiceStreamManager streamManager)
    {
        _activeStreams[sourceEntity] = streamManager;
    }

    public void Shutdown()
    {
        _clientNetManager.ClientConnectStateChanged -= OnConnectStateChanged;
        _clientNetManager.Connected -= OnConnected;
        _clientNetManager.Disconnect -= OnDisconnected;
        ClearSessionFile();

        _cfg.UnsubValueChanged(GoobCVars.VoiceChatVolume, OnVolumeChanged);
        _cfg.UnsubValueChanged(GoobCVars.VoiceChatHearSelf, OnHearSelfChanged);

        foreach (var stream in _activeStreams.Values)
        {
            stream.Dispose();
        }
        _activeStreams.Clear();

        _sawmill.Info("VoiceChatClientManager has been shut down");
    }

    /// <inheritdoc/>
    public void Update()
    {
        if (_lastLocalSpeechTime != TimeSpan.Zero && _timing.CurTime - _lastLocalSpeechTime > TimeSpan.FromSeconds(0.35))
        {
            _lastLocalSpeechTime = TimeSpan.Zero;
            OnLocalSpeaking?.Invoke(false, 0f);
        }

        if (_clientNetManager.IsConnected && (_lastWrittenSession.Contains("\"userId\": \"\"") || string.IsNullOrEmpty(_lastWrittenSession)))
        {
            UpdateSessionFile();
        }

        List<EntityUid>? toRemove = null;

        foreach (var (uid, stream) in _activeStreams)
        {
            stream.Update();

            if (!_entityManager.EntityExists(uid))
            {
                toRemove ??= new List<EntityUid>();
                toRemove.Add(uid);
            }
        }

        if (toRemove != null)
        {
            foreach (var uid in toRemove)
            {
                if (_activeStreams.TryGetValue(uid, out var stream))
                {
                    _sawmill.Debug($"Removing voice stream for deleted entity {uid}");
                    stream.Dispose();
                    _activeStreams.Remove(uid);
                }
            }
        }
    }
}
