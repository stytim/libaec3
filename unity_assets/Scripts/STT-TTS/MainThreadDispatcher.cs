using UnityEngine;
using System.Collections.Concurrent;
using System;

public class MainThreadDispatcher : MonoBehaviour
{
    private static readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();

    public static void Enqueue(Action action)
    {
        _executionQueue.Enqueue(action);
    }

    void Update()
    {
        while (_executionQueue.TryDequeue(out var action))
        {
            action.Invoke();
        }
    }

    // This automatically adds the dispatcher to the scene if it doesn't exist
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Initialize()
    {
        if (FindFirstObjectByType<MainThreadDispatcher>() == null)
        {
            var go = new GameObject("MainThreadDispatcher");
            go.AddComponent<MainThreadDispatcher>();
            DontDestroyOnLoad(go);
        }
    }
}