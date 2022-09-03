using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Narabemi.Messages
{
    public enum SimpleMessageType
    {
        Play,
        Reopen,
        FrameForward,
        FrameBackward,
    }

    public class SimpleMessageData
    {
        public SimpleMessageType MessageType { get; }
        public int? PlayerId { get; }

        public SimpleMessageData(SimpleMessageType messageType, int? playerId = null)
        {
            MessageType = messageType;
            PlayerId = playerId;
        }
    }

    public class SimpleMessage : ValueChangedMessage<SimpleMessageData>
    {
        public SimpleMessage(SimpleMessageType messageType, int? playerId) : base(new(messageType, playerId)) { }
    }
}
