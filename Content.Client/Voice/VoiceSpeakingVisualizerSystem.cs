using System.Numerics;
using Content.Client.Voice;
using Robust.Client.GameObjects;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client.Voice;

/// <summary>
/// Displays an in-world animated speech bubble above entities when they speak via voice chat.
/// </summary>
public sealed class VoiceSpeakingVisualizerSystem : EntitySystem
{
    [Dependency] private readonly IVoiceChatManager _voiceManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;

    private enum VoiceVisualLayers : byte
    {
        VoiceSpeechBubble
    }

    private readonly Dictionary<EntityUid, TimeSpan> _activeSpeakers = new();
    private readonly List<EntityUid> _toRemove = new();

    public override void Initialize()
    {
        base.Initialize();
        _voiceManager.OnEntitySpeaking += OnEntitySpeaking;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _voiceManager.OnEntitySpeaking -= OnEntitySpeaking;

        foreach (var uid in _activeSpeakers.Keys)
        {
            HideBubble(uid);
        }
        _activeSpeakers.Clear();
    }

    private void OnEntitySpeaking(EntityUid uid, float amplitude)
    {
        _activeSpeakers[uid] = _timing.CurTime + TimeSpan.FromSeconds(0.4);

        if (!TryComp<SpriteComponent>(uid, out var sprite))
            return;

        var layerExists = _sprite.LayerMapTryGet((uid, sprite), VoiceVisualLayers.VoiceSpeechBubble, out var layer, false);
        if (!layerExists)
        {
            layer = _sprite.LayerMapReserve((uid, sprite), VoiceVisualLayers.VoiceSpeechBubble);
            _sprite.LayerSetRsi((uid, sprite), layer, new ResPath("/Textures/Effects/speech.rsi"), "default0");
            _sprite.LayerSetOffset((uid, sprite), layer, new Vector2(0, 1.0f));
        }

        _sprite.LayerSetVisible((uid, sprite), layer, true);
    }

    private void HideBubble(EntityUid uid)
    {
        if (TryComp<SpriteComponent>(uid, out var sprite))
        {
            if (_sprite.LayerMapTryGet((uid, sprite), VoiceVisualLayers.VoiceSpeechBubble, out var layer, false))
            {
                _sprite.LayerSetVisible((uid, sprite), layer, false);
            }
        }
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_activeSpeakers.Count == 0)
            return;

        var curTime = _timing.CurTime;
        _toRemove.Clear();

        foreach (var (uid, expireTime) in _activeSpeakers)
        {
            if (curTime > expireTime)
            {
                _toRemove.Add(uid);
                HideBubble(uid);
            }
        }

        foreach (var uid in _toRemove)
        {
            _activeSpeakers.Remove(uid);
        }
    }
}
