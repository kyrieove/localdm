(async () => {
  const out = {};
  const page = await fetch('/tablet/role/pages/1');
  out.rolePageStatus = page.status;
  out.rolePageType = page.headers.get('content-type');
  const buf = new Uint8Array(await page.arrayBuffer());
  out.rolePageSignature = Array.from(buf.slice(0, 8)).join(',');
  const conflict = await fetch('/tablet/claim?roleId=role-1', { method: 'POST' });
  out.claimTakenStatus = conflict.status;
  document.cookie = 'localdm-tablet-spike=; Max-Age=0; path=/';
  const anon = await fetch('/tablet/role/pages/1');
  out.anonymousPageStatus = anon.status;
  const traversal = await fetch('/tablet/cards/..%2F..%2Fcard-1/front');
  out.traversalStatus = traversal.status;
  return JSON.stringify(out);
})()
