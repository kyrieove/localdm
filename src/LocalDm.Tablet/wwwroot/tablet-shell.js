window.tabletClipboard = {
  async copy(text) {
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
        return true;
      }
      const input = document.createElement('textarea');
      input.value = text;
      input.style.position = 'fixed';
      input.style.left = '-9999px';
      document.body.append(input);
      input.select();
      try { return document.execCommand('copy'); }
      finally { input.remove(); }
    } catch {
      return false;
    }
  },
};
