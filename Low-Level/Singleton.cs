using UnityEngine;

public abstract class StaticInstance<T> : MonoBehaviour where T : MonoBehaviour
{
    public static T Instance { get; private set; }
    protected virtual void Awake() => Instance = this as T;

    protected virtual void OnApplicationQuit()
    {
        Instance = null;
        Destroy(gameObject);
    }

}

public abstract class Singleton<T> : StaticInstance<T> where T : MonoBehaviour
{
    protected override void Awake()
    {
        if (Instance != null && Instance != this) Destroy(Instance);
        base.Awake();
    }
}


public abstract class PersistentSingleton<T> : Singleton<T> where T : MonoBehaviour
{
    protected override void Awake()
    {
        base.Awake();
        DontDestroyOnLoad(gameObject);
    }
}

public abstract class StaticScriptableObject<T> : ScriptableObject where T : ScriptableObject
{
    private static T instance;

    public static T Instance {
        get {
            if (instance == null) {
                T[] assets = Resources.LoadAll<T>(""); 
                if (assets.Length > 0) instance = assets[0];
                else Debug.LogError($"[StaticScriptableObject] Could not find {typeof(T).Name} in any Resources folder.");
                instance.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            return instance;
        }
    }
    
    protected virtual void OnEnable()
    {
        if (instance == null) instance = this as T;
        hideFlags = HideFlags.DontUnloadUnusedAsset;
    }

    protected virtual void OnDisable()
    {
        if (instance == this) instance = null;
    }
}