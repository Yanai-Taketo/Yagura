// ステール警告の自律監視（ui.md §5.2。M8-2）。
//
// 本ファイルは ADR-0003 決定 1（JS を原則手書きしない）に対して ui.md §5.2 が設計上確定した
// 「分離 JS モジュール」の例外である（§11 のページ固有 CSS 例外台帳の対象外）。
// 理由: サーバ停止・circuit 回復不能の局面でこそステール警告が要るため、警告の検知と描画を
// サーバ側レンダリング（Blazor circuit）に依存させられない。
//
// 動作: init() で監視を開始し、閾値（thresholdMs。仮値 = 更新間隔の 3 倍——UI-2）を超えて
// notifyUpdated() が呼ばれなければ、overlay 要素を表示する。タイマーはすべてブラウザ内で
// 完結し、サーバとの通信を行わない（閲覧リスナの配信面を増やさない——ui.md §4）。

export function init(overlay, thresholdMs, staleTitle) {
  const state = {
    lastUpdated: Date.now(),
    title: staleTitle,
    timer: null,
  };

  const check = () => {
    if (Date.now() - state.lastUpdated <= thresholdMs) {
      return;
    }
    for (const el of overlay.querySelectorAll("[data-yagura-stale-title]")) {
      el.textContent = state.title;
    }
    overlay.classList.add("yagura-stale-visible");
    overlay.setAttribute("aria-hidden", "false");
  };

  // 監視間隔は閾値の 1/10（最短 1 秒）——閾値超過から表示までの遅延を閾値の 1 割以内に抑える。
  state.timer = setInterval(check, Math.max(1000, thresholdMs / 10));

  return {
    // データ受信の通知（起点 = クライアントが最後にデータを受信した時刻——ui.md §5.2）。
    notifyUpdated(staleTitle) {
      state.lastUpdated = Date.now();
      state.title = staleTitle;
      overlay.classList.remove("yagura-stale-visible");
      overlay.setAttribute("aria-hidden", "true");
    },
    dispose() {
      if (state.timer !== null) {
        clearInterval(state.timer);
        state.timer = null;
      }
    },
  };
}
