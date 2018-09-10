using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Net;
using System.Collections.Generic;
using System.Threading;

namespace StreamingMesh.Core.Threading
{
  [ExecuteInEditMode]
  public sealed class ThreadManager: MonoBehaviour
  {
    static ThreadManager m_Instance;

    ///<summary>
    ///Sequential Execution in Update() or ThreadUpdate()
    ///</summary>
    readonly Queue<Action> httpAction = new Queue<Action>();
    readonly Queue<Action> processAction = new Queue<Action>();

    bool m_IsLastHttpActionFinished = true; // do not change
    bool m_IsLastProcessActionFinished = true; // do not change

    bool m_IsApplicationPlaying = false;
    
    public static ThreadManager Instance
    {
      get
      {
        if(m_Instance == null)
        {
          GameObject obj = new GameObject();
          obj.name = "STMThreadManager";
          obj.hideFlags = HideFlags.HideInHierarchy;
          m_Instance = obj.AddComponent<ThreadManager>();
        }
        return m_Instance;
      }
    }

    public void PushHttpAction(Action action) {
     httpAction.Enqueue(action);
    }

    public void PushProcessAction(Action action) {
      processAction.Enqueue(action);
    }

    public void FinishHttpAction() {
      m_IsLastHttpActionFinished = true;
    }

    public void FinishProcessAction() {
      m_IsLastProcessActionFinished = true;
    }

    void Start() {
      m_IsApplicationPlaying = true;
    }

#if UNITY_EDITOR

    Thread m_Thread;

    void OnEnable()
    {
      m_Thread = new Thread(ThreadUpdate);
      try {
          m_Thread.Start();
      } catch (ThreadStartException ex) {
          Debug.LogError(ex.Source);
      }
    }

    void OnDisable()
    {
      #if UNITY_EDITOR
      if (m_Thread != null)
      {
        m_IsApplicationPlaying = false;
        m_Thread.Abort();
        m_Thread = null;
      }
      #endif
    }

    ///<summary>
    ///Sequential Execution if an application is not running
    ///</summary>
    void ThreadUpdate()
    {
      while(true)
      {
        Thread.Sleep(100);
        lock(httpAction)
        {
          if (httpAction.Count > 0 && !m_IsApplicationPlaying && m_IsLastHttpActionFinished)
          {
            m_IsLastHttpActionFinished = false;
            httpAction.Dequeue().Invoke();
          }
          if (processAction.Count > 0 && !m_IsApplicationPlaying && m_IsLastProcessActionFinished)
          {
            m_IsLastProcessActionFinished = false;
            processAction.Dequeue().Invoke();
          }
        }
      }
    }

#endif

    ///<summary>
    ///Sequential Execution if an application is running
    ///</summary>
    void Update()
    {
        if (httpAction.Count > 0 && m_IsApplicationPlaying && m_IsLastHttpActionFinished)
        {
          m_IsLastHttpActionFinished = false;
          httpAction.Dequeue().Invoke();
        }
        if (processAction.Count > 0 && m_IsApplicationPlaying && m_IsLastProcessActionFinished)
        {
          m_IsLastProcessActionFinished = false;
          processAction.Dequeue().Invoke();
        }
    }

  }
}
