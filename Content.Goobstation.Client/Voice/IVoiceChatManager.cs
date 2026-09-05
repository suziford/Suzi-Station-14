// SPDX-License-Identifier: AGPL-3.0-or-later

using System;

namespace Content.Goobstation.Client.Voice;

/// <summary>
/// Extended interface for Goobstation voice chat manager.
/// </summary>
public interface IVoiceChatManager : Content.Client.Voice.IVoiceChatManager
{
    bool TryGetStreamManager(EntityUid sourceEntity, out VoiceStreamManager streamManager);
    void AddStreamManager(EntityUid sourceEntity, VoiceStreamManager streamManager);
}
