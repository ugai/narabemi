using System;

namespace Narabemi
{
    public enum GlobalPlaybackState
    {
        Init,
        Play,
        Pause,
        Stop,
    }

    public static class GlobalPlaybackStateExtension
    {
        public static GlobalPlaybackState TogglePlayPause(this GlobalPlaybackState state)
        {
            return state switch
            {
                GlobalPlaybackState.Init => GlobalPlaybackState.Play,
                GlobalPlaybackState.Play => GlobalPlaybackState.Pause,
                GlobalPlaybackState.Pause => GlobalPlaybackState.Play,
                GlobalPlaybackState.Stop => GlobalPlaybackState.Play,
                _ => throw new NotImplementedException(),
            };
        }
    }
}
