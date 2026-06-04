using UnityEngine;

namespace NeKoRoSYS.InputHandling
{
    public class Bootstrapper : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            GameObject runnerObject = new("[Hidden] InputReader Runner")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(runnerObject);
            runnerObject.AddComponent<InputReaderBootstrapper>();
            Debug.Log("[Input System] Global Input Runner initialized.");
        }

        private void Update()
        {
            if (InputReader.Instance == null) return;
            InputReader.Instance.TickInputBuffers(Time.deltaTime);
            InputReader.Instance.FlushSnapshot(false);
        }
    }
}
