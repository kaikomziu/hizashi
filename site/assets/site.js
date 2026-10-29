'use strict';

// スマホ用メニューの開閉
(() => {
  const btn = document.querySelector('.nav-toggle');
  const nav = document.getElementById('site-nav');
  if (!btn || !nav) return;
  btn.addEventListener('click', () => {
    const open = nav.classList.toggle('open');
    btn.setAttribute('aria-expanded', String(open));
  });
})();

// ダウンロードページ: 最新リリースの情報を GitHub から取得して表示する。
// 取得できないとき（オフライン・API制限）は、ページに書いてある内容のまま表示される。
// ※ このときだけ、閲覧者のブラウザから api.github.com へ通信する（プライバシーのページに記載）。
(async () => {
  const box = document.querySelector('[data-latest]');
  if (!box) return;
  try {
    const r = await fetch('https://api.github.com/repos/kaikomziu/hizashi/releases/latest', {
      headers: { Accept: 'application/vnd.github+json' },
    });
    if (!r.ok) throw new Error(String(r.status));
    const j = await r.json();
    const exe = (j.assets || []).find((a) => a.name === 'Hizashi.exe');
    const set = (key, text) => document.querySelectorAll(`[data-${key}]`).forEach((el) => { el.textContent = text; });
    set('version', j.tag_name);
    if (j.published_at) set('date', new Date(j.published_at).toLocaleDateString('ja-JP', { year: 'numeric', month: 'long', day: 'numeric' }));
    if (exe) set('size', `${(exe.size / 1048576).toFixed(1)} MB`);
    const m = (j.body || '').match(/SHA-256:\s*`?([0-9a-f]{64})`?/i);
    if (m) set('sha', m[1].toLowerCase());
  } catch (e) {
    /* 静的な表示のままにする */
  }
})();
