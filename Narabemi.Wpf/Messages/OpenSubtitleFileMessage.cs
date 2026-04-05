using System;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Narabemi.Messages
{
    public class OpenSubtitleFileMessageData
    {
        public int PlayerId { get; }
        public string Path { get; }

        public OpenSubtitleFileMessageData(int playerId, string path)
        {
            PlayerId = playerId;
            Path = path;
        }
    }

    public class OpenSubtitleFileMessage : ValueChangedMessage<OpenSubtitleFileMessageData>
    {
        public OpenSubtitleFileMessage(int playerId, string path) : base(new(playerId, path)) { }
    }
}
