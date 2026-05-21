using CommunityToolkit.Mvvm.Messaging.Messages;
using Narabemi.Models;

namespace Narabemi.Messages
{
    public class PlaybackStateChangedMessage : ValueChangedMessage<GlobalPlaybackState>
    {
        public PlaybackStateChangedMessage(GlobalPlaybackState state) : base(state) { }
    }
}
