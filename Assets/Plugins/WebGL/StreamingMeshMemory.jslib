mergeInto(LibraryManager.library, {
  STM_ReceiverMemorySnapshot: function(stagePtr, capacityPtr, allocatedPtr) {
    Module.StreamingMeshMemoryStage = UTF8ToString(stagePtr);
    var metrics = _getMetricsInfo() >>> 0;
    HEAPU32[capacityPtr >>> 2] = wasmMemory.buffer.byteLength;
    HEAPU32[allocatedPtr >>> 2] = HEAPU32[(metrics + 4) >>> 2];
  }
});
