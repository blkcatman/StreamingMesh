mergeInto(LibraryManager.library, {
  STM_TextureProbeReport: function(jsonPtr) {
    var element = document.getElementById('texture-probe-result');
    if (!element) {
      element = document.createElement('pre');
      element.id = 'texture-probe-result';
      element.style.cssText = 'white-space:pre-wrap;background:#161820;color:#eee;padding:16px;max-width:960px;margin:auto';
      document.body.appendChild(element);
    }
    element.textContent = UTF8ToString(jsonPtr);
  }
});
