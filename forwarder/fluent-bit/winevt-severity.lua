-- winevt-severity.lua — Windows イベントログのレコードを syslog 転送向けに変換する
-- 1) Level を syslog severity へ変換
--    Level: 1=Critical 2=Error 3=Warning 4=Information 5=Verbose
--    severity: 2=crit 3=err 4=warning 6=info 7=debug
-- 2) EventID / Channel を RFC 5424 STRUCTURED-DATA 用のマップへ集める
--    (out_syslog の Syslog_SD_Key winevt と対 → [winevt EventID="..." Channel="..."])
-- 3) APP-NAME 用にソース名(ProviderName)を無害化する
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

function enrich_winevt(tag, timestamp, record)
    local level = tonumber(record["Level"])
    local severity = level_to_severity[level]
    if severity == nil then
        -- Level 0 (LogAlways) や未知値は info 扱い
        severity = 6
    end
    record["SyslogSeverity"] = severity

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
