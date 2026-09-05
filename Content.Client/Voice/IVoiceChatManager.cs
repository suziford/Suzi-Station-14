// SPDX-License-Identifier: AGPL-3.0-or-later

using System;

namespace Content.Client.Voice;

/// <summary>
/// Interface for the voice chat manager.
/// </summary>
public interface IVoiceChatManager
{
    /// <summary>
    /// Adds a packet of PCM audio data to the playback queue for a specific entity.
    /// </summary>
    void AddPacket(EntityUid sourceEntity, byte[] pcmData);

    /// <summary>
    /// Event fired when an entity is speaking audio, along with the sound amplitude (0.0 to 1.0).
    /// </summary>
    event Action<EntityUid, float>? OnEntitySpeaking;

    /// <summary>
    /// Event fired when the local player is speaking or silent, along with sound amplitude.
    /// </summary>
    event Action<bool, float>? OnLocalSpeaking;

    void Initalize();
    void Update();
    void Shutdown();
}
