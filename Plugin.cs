using IPA;
using UnityEngine;

namespace BSProfiler
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        internal static IPA.Logging.Logger? Log { get; private set; }
        private GameObject? _owner;

        [Init]
        public void Init(IPA.Logging.Logger logger) => Log = logger;

        [OnStart]
        public void Start()
        {
            _owner = new GameObject("BSProfiler");
            Object.DontDestroyOnLoad(_owner);
            _owner.AddComponent<ProfilerBehaviour>();
        }

        [OnExit]
        public void Stop()
        {
            if (_owner != null)
            {
                _owner.GetComponent<ProfilerBehaviour>()?.StopCapture(true);
                Object.Destroy(_owner);
                _owner = null;
            }
        }
    }
}
