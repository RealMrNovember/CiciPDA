// Fills in the current version and a direct installer link from the latest GitHub release.
// The page works without this script: the buttons already point at the latest-release page.
(function () {
  document.getElementById('yr').textContent = new Date().getFullYear();
  fetch('https://api.github.com/repos/RealMrNovember/CiciPDA/releases/latest', { headers: { Accept: 'application/vnd.github+json' } })
    .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
    .then(function (rel) {
      var asset = (rel.assets || []).find(function (a) { return /windows-x64-Setup\.exe$/i.test(a.name); });
      var ver = (rel.tag_name || '').replace(/^v/, '');
      if (!asset || !ver) return;
      document.querySelectorAll('[data-dl]').forEach(function (a) {
        if (a.id === 'heroDl' || a.id === 'dlWin') a.href = asset.browser_download_url;
      });
      ['heroVer', 'dlVer'].forEach(function (id) { var e = document.getElementById(id); if (e) e.textContent = 'v' + ver; });
      var mb = (asset.size / 1048576).toFixed(0) + ' MB';
      var hs = document.getElementById('heroSize'); if (hs) hs.textContent = mb;
      var dm = document.getElementById('dlMeta'); if (dm) dm.textContent = 'Setup .exe · ' + mb + ' · v' + ver;
    })
    .catch(function () { /* keep the static links */ });
})();
