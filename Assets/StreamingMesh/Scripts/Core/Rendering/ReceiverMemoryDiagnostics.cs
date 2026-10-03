using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Profiling;

namespace StreamingMesh.Core.Rendering
{
  // Opt-in, allocating diagnostic snapshots; never called in the normal hot path.
  public static class ReceiverMemoryDiagnostics
  {
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void STM_ReceiverMemorySnapshot(string stage, out uint capacity, out uint allocated);
#endif
    [Serializable] sealed class Snapshot
    {
      public string stage, detail;
      public double seconds;
      public long bytes, wasmCapacity, wasmAllocated, monoHeap, monoUsed, nativeAllocated, nativeReserved;
      public int gcCount;
      public string buffers;
    }
    public static void Log(string stage, long bytes = 0, string detail = "", StreamingMeshRenderer renderer = null)
    {
      uint capacity = 0, allocated = 0;
#if UNITY_WEBGL && !UNITY_EDITOR
      STM_ReceiverMemorySnapshot(stage, out capacity, out allocated);
#endif
      var snapshot = new Snapshot {stage=stage, detail=detail, seconds=Time.realtimeSinceStartupAsDouble,
        bytes=bytes, wasmCapacity=capacity, wasmAllocated=allocated,
        monoHeap=Profiler.GetMonoHeapSizeLong(), monoUsed=Profiler.GetMonoUsedSizeLong(),
        nativeAllocated=Profiler.GetTotalAllocatedMemoryLong(), nativeReserved=Profiler.GetTotalReservedMemoryLong(),
        gcCount=GC.CollectionCount(0), buffers=renderer == null ? "" : renderer.MemoryDiagnosticsDetails()};
      Debug.Log("STM_MEM " + JsonUtility.ToJson(snapshot));
    }
  }
}
