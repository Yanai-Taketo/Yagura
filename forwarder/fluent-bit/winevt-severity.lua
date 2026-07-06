-- winevt-severity.lua — Windows イベントログのレコードを syslog 転送向けに変換する
-- 1) Level を syslog severity へ変換
--    Level: 1=Critical 2=Error 3=Warning 4=Information 5=Verbose
--    severity: 2=crit 3=err 4=warning 6=info 7=debug
-- 2) EventID / Channel を RFC 5424 STRUCTURED-DATA 用のマップへ集める
--    (out_syslog の Syslog_SD_Key winevt と対 → [winevt EventID="..." Channel="..."])
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
    if next(sd) ~= nil then
        record["winevt"] = sd
    end

    return 1, timestamp, record
end
