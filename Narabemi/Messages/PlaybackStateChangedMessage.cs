using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Narabemi.Messages
{
    public class PlaybackStateChangedMessage : ValueChangedMessage<GlobalPlaybackState>
    {
        public PlaybackStateChangedMessage(GlobalPlaybackState state) : base(state) { }
    }
}
