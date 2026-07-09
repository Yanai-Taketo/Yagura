-- winevt-severity.lua — Windows イベントログのレコードを syslog 転送向けに変換する
-- 1) Level を syslog severity へ変換
--    Level: 1=Critical 2=Error 3=Warning 4=Information 5=Verbose
--    severity: 2=crit 3=err 4=warning 6=info 7=debug
-- 1b) Keywords の Audit Failure ビットを severity へ反映する(Issue #153)
--    Security チャネルの監査イベント(失敗ログオン 4625 等)の多くは Level=0
--    (LogAlways)で送られ、成功/失敗は Level ではなく Keywords に符号化されている。
--    Level だけを見ると severity=6(info)に落ち、受信側の重大度フィルタ/アラートに
--    乗らない。WINEVENT_KEYWORD_AUDIT_FAILURE(bit52 = 0x0010000000000000)が
--    立っていれば、Level 由来の severity が warning(4)より弱い(数値が大きい)場合に
--    warning(4)へ引き上げる(Level 由来が既に crit/err 等より強ければそちらを維持する
--    ——監査失敗の検知を「格下げ」しない片方向の底上げ)。
-- 2) チャネル別に facility を付与する(Issue #154)
--    facility を与えないと out_syslog の既定 facility=user(1) に全チャネルが潰れる。
--    Security は認証関連の定石である authpriv(10)、System は daemon(3)、
--    Application はその他アプリ用途の定石である local0(16)に分ける。
--    未知のチャネル(将来 KnownChannels が増えた場合の保険)は user(1)を維持する。
-- 3) EventID / Channel を RFC 5424 STRUCTURED-DATA 用のマップへ集める
--    (out_syslog の Syslog_SD_Key winevt と対 → [winevt EventID="..." Channel="..."])
-- 4) APP-NAME 用にソース名(ProviderName)を無害化する
--    RFC 5424 の APP-NAME は空白を含まない PRINTUSASCII・最大 48 文字。
--    「Service Control Manager」のような空白入りソース名をそのまま送ると
--    受信側の区切り解釈がずれてフィールドが壊れるため、範囲外の文字を _ に
--    置換して送る。変換が生じた場合のみ、元の名前を Provider として
--    STRUCTURED-DATA に保持する(情報を欠落させない)。
local level_to_severity = {
    [1] = 2,
    [2] = 3,
    [3] = 4,
    [4] = 6,
    [5] = 7,
}

-- WINEVENT_KEYWORD_AUDIT_FAILURE (bit52 = 0x0010000000000000) を昇格させる先の severity。
local AUDIT_FAILURE_SEVERITY = 4

-- チャネル名 → out_syslog Syslog_Facility_Key 用の facility 番号。
local channel_to_facility = {
    ["Security"] = 10,    -- authpriv
    ["System"] = 3,       -- daemon
    ["Application"] = 16, -- local0
}
local DEFAULT_FACILITY = 1 -- user（未知チャネルの保険）

-- Keywords の Audit Failure ビット(bit52)が立っているかを判定する。
--
-- 精度についての注意: fluent-bit の winevtlog 入力プラグイン(plugins/in_winevtlog/pack.c の
-- pack_keywords)は Keywords を 64bit 整数のまま msgpack へ積まず、"0x" + 16進文字列
-- (例 "0x8010000000000000". 先頭ゼロは付与されない)として cstring で積む。これを
-- tonumber() 等で Lua の数値(倍精度浮動小数点・仮数部53bit)へ変換すると、2^53 を超える
-- 64bit 値の下位ビットが丸められて破壊されうるため、判定は文字列のまま行う
-- (数値へ一切変換しない)。万一 Keywords が数値型で渡ってきた場合(未知の入力経路への保険)
-- のみ、判定対象ビット(2^52)は倍精度の仮数部(2^53未満)に収まるため、除算・剰余による
-- 判定でも実用上壊れない——ただし主経路ではなくあくまで保険であることをコメントで明示する。
local function has_audit_failure_keyword(record)
    local keywords = record["Keywords"]
    if keywords == nil then
        return false
    end

    if type(keywords) == "string" then
        -- "0x" + 16進文字列(先頭ゼロなし)を左ゼロ埋めして16桁に揃え、Audit Failure ビット
        -- (bit52)を含むニブル(先頭から3桁目、0 始まりで桁位置2)だけを取り出す。
        -- ニブル自体は 0-15 の範囲なので tonumber() での数値化に精度損失は生じない。
        local hex = keywords:match("^0[xX](%x+)$")
        if hex == nil then
            return false
        end
        if #hex < 16 then
            hex = string.rep("0", 16 - #hex) .. hex
        end
        local nibble = tonumber(string.sub(hex, 3, 3), 16)
        return nibble ~= nil and (nibble % 2) == 1
    end

    if type(keywords) == "number" then
        -- 保険経路(上記コメント参照)。2^52 ビットの有無だけを除算・剰余で確認する。
        local shifted = math.floor(keywords / 0x10000000000000)
        return math.floor(shifted) % 2 == 1
    end

    return false
end

function enrich_winevt(tag, timestamp, record)
    local level = tonumber(record["Level"])
    local severity = level_to_severity[level]
    if severity == nil then
        -- Level 0 (LogAlways) や未知値は info 扱い
        severity = 6
    end

    if has_audit_failure_keyword(record) and severity > AUDIT_FAILURE_SEVERITY then
        severity = AUDIT_FAILURE_SEVERITY
    end
    record["SyslogSeverity"] = severity

    local channel = record["Channel"]
    if channel ~= nil then
        record["SyslogFacility"] = channel_to_facility[tostring(channel)] or DEFAULT_FACILITY
    else
        record["SyslogFacility"] = DEFAULT_FACILITY
    end

    local sd = {}
    if record["EventID"] ~= nil then
        sd["EventID"] = tostring(record["EventID"])
    end
    if record["Channel"] ~= nil then
        sd["Channel"] = tostring(record["Channel"])
    end

    local provider = record["ProviderName"]
    if provider ~= nil then
        provider = tostring(provider)
        -- 空白を含む印字可能 ASCII(0x21-0x7E)以外を _ へ(バイト単位)
        local appname = provider:gsub("[^\33-\126]", "_")
        appname = string.sub(appname, 1, 48)
        if appname == "" then
            appname = "-"
        end
        record["SyslogAppname"] = appname
        if appname ~= provider then
            sd["Provider"] = provider
        end
    end

    if next(sd) ~= nil then
        record["winevt"] = sd
    end

    return 1, timestamp, record
end
