// フォワーダ MSI アップロード（ADR-0020 決定 3。配置経路 (b)）。
//
// アップロード本文を SignalR circuit（Blazor の InputFile ストリーミング）に載せてはならない
// —— circuit のメッセージサイズ上限の緩和は新しい DoS 面になるため禁止（ADR-0020 決定 3）。
// 代わりにブラウザの fetch で専用エンドポイント（POST /admin/forwarder-kit/msi/stage）へ
// 直接送る。File オブジェクトを body に渡すことでブラウザがストリーミング送信し、
// Content-Length も自動で申告される（サーバは申告値で事前拒否 + 累積カウントで打ち切りの二段）。
//
// CSRF: 認証セッション Cookie で保護された状態変更操作のため、antiforgery トークンを
// 専用エンドポイント（GET /admin/forwarder-kit/msi/antiforgery）から取得し、ヘッダーで送る。

/**
 * 選択されたファイルのサイズを送信前に検査する（ADR-0020 決定 3——サイズ超過は送信前に
 * クライアント側で分かるようにする。PR #167 佐藤質問 3）。
 * @returns {{ ok: boolean, error?: string, size?: number }}
 */
export function precheck(inputElement, maxBytes) {
  const file = inputElement && inputElement.files && inputElement.files[0];
  if (!file) {
    return { ok: false, error: "no-file" };
  }
  if (file.size > maxBytes) {
    return { ok: false, error: "file-too-large", size: file.size };
  }
  return { ok: true, size: file.size };
}

/**
 * ステージングへアップロードする。
 * @returns {{ status: number, ok: boolean, body: object | null }}
 */
export async function stage(inputElement, architecture) {
  const file = inputElement && inputElement.files && inputElement.files[0];
  if (!file) {
    return { status: 0, ok: false, body: { error: "no-file" } };
  }

  const tokenResponse = await fetch("/admin/forwarder-kit/msi/antiforgery", {
    credentials: "same-origin",
  });
  if (!tokenResponse.ok) {
    return { status: tokenResponse.status, ok: false, body: { error: "token-fetch-failed" } };
  }
  const tokenInfo = await tokenResponse.json();

  const headers = { "Content-Type": "application/octet-stream" };
  headers[tokenInfo.headerName || "RequestVerificationToken"] = tokenInfo.token;

  const response = await fetch(
    "/admin/forwarder-kit/msi/stage?architecture=" + encodeURIComponent(architecture),
    {
      method: "POST",
      credentials: "same-origin",
      headers: headers,
      body: file,
    });

  let body = null;
  try {
    body = await response.json();
  } catch {
    // 応答が JSON でない（プロキシ・切断等）——status だけで判定する。
  }

  return { status: response.status, ok: response.ok, body: body };
}
