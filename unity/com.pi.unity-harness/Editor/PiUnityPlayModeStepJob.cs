using System;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// Enter or exit PlayMode and complete only after the transition settles. Entering
    /// PlayMode normally reloads the domain, so the wait continues in the new domain.
    /// </summary>
    internal sealed class PiUnityPlayModeStepJob : IStepJobHandler
    {
        internal const string KindName = "playmode";
        internal const string TargetEnter = "enter";
        internal const string TargetExit = "exit";

        private const int StepRequest = 0;
        private const int StepWait = 1;
        // Frames after isPlaying turns true, so Awake/Start/first Update have run.
        private const int SettleFrames = 2;

        internal static Func<bool> IsPlaying = () => EditorApplication.isPlaying;
        internal static Func<bool> IsPlayingOrWillChange = () => EditorApplication.isPlayingOrWillChangePlaymode;
        internal static Func<bool> IsPaused = () => EditorApplication.isPaused;
        internal static Func<bool> IsBusy = () => EditorApplication.isCompiling || EditorApplication.isUpdating;
        internal static Func<int> FrameCount = () => Time.frameCount;
        internal static Action<bool> SetPlaying = value => EditorApplication.isPlaying = value;

        [Serializable]
        private sealed class Data
        {
            public string target;
            public bool alreadyInState;
            public int firstPlayingFrame = -1;
        }

        [Serializable]
        private sealed class Result
        {
            public string target;
            public bool isPlaying;
            public bool isPaused;
            public bool alreadyInState;
            public int reloads;
        }

        public string Kind
        {
            get { return KindName; }
        }

        internal static string CreateData(string target)
        {
            return JsonUtility.ToJson(new Data { target = target });
        }

        public StepOutcome Step(StepJobState state)
        {
            Data data = Read(state);
            bool enter = string.Equals(data.target, TargetEnter, StringComparison.Ordinal);
            if (!enter && !string.Equals(data.target, TargetExit, StringComparison.Ordinal))
                return StepOutcome.Fail("usage", "unknown playmode target: " + data.target);

            if (state.step == StepRequest)
            {
                if (IsPlaying() == enter && IsPlayingOrWillChange() == enter)
                {
                    data.alreadyInState = true;
                    return StepOutcome.Done(BuildResult(state, data));
                }

                SetPlaying(enter);
                state.step = StepWait;
                Write(state, data);
                return StepOutcome.Continue();
            }

            if (!enter)
            {
                if (IsPlaying() || IsPlayingOrWillChange() || IsBusy())
                    return StepOutcome.Continue();
                return StepOutcome.Done(BuildResult(state, data));
            }

            if (!IsPlayingOrWillChange())
                return StepOutcome.Fail("playmode_rejected", "Unity did not enter PlayMode; check the Console for compile errors or a blocking modal.");
            if (!IsPlaying() || IsBusy())
                return StepOutcome.Continue();
            if (IsPaused())
                return StepOutcome.Done(BuildResult(state, data));

            if (data.firstPlayingFrame < 0)
            {
                data.firstPlayingFrame = FrameCount();
                Write(state, data);
                return StepOutcome.Continue();
            }

            if (FrameCount() - data.firstPlayingFrame < SettleFrames)
                return StepOutcome.Continue();
            return StepOutcome.Done(BuildResult(state, data));
        }

        public void OnRestored(StepJobState state)
        {
            Data data = Read(state);
            data.firstPlayingFrame = -1;
            Write(state, data);
        }

        internal static void ResetForTests()
        {
            IsPlaying = () => EditorApplication.isPlaying;
            IsPlayingOrWillChange = () => EditorApplication.isPlayingOrWillChangePlaymode;
            IsPaused = () => EditorApplication.isPaused;
            IsBusy = () => EditorApplication.isCompiling || EditorApplication.isUpdating;
            FrameCount = () => Time.frameCount;
            SetPlaying = value => EditorApplication.isPlaying = value;
        }

        private static string BuildResult(StepJobState state, Data data)
        {
            return JsonUtility.ToJson(new Result
            {
                target = data.target,
                isPlaying = IsPlaying(),
                isPaused = IsPaused(),
                alreadyInState = data.alreadyInState,
                reloads = state.reloads,
            });
        }

        private static Data Read(StepJobState state)
        {
            Data data = string.IsNullOrEmpty(state.data) ? null : JsonUtility.FromJson<Data>(state.data);
            return data ?? new Data();
        }

        private static void Write(StepJobState state, Data data)
        {
            state.data = JsonUtility.ToJson(data);
        }
    }
}
