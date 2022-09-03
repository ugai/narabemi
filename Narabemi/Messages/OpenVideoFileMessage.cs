using System;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Narabemi.Messages
{
    public class OpenVideoFileMessageData
    {
        public int PlayerId { get; }
        public Uri Uri { get; }

        public OpenVideoFileMessageData(int playerId, Uri uri)
        {
            PlayerId = playerId;
            Uri = uri;
        }
    }

    public class OpenVideoFileMessage : ValueChangedMessage<OpenVideoFileMessageData>
    {
        public OpenVideoFileMessage(int playerId, Uri uri) : base(new(playerId, uri)) { }
    }
}
