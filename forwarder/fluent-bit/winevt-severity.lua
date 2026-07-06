-- winevt-severity.lua — Windows イベントログの Level を syslog severity へ変換する
-- Level: 1=Critical 2=Error 3=Warning 4=Information 5=Verbose
-- severity: 2=crit 3=err 4=warning 6=info 7=debug
local level_to_severity = {
    [1] = 2,
    [2] = 3,
    [3] = 4,
    [4] = 6,
    [5] = 7,
}

function add_severity(tag, timestamp, record)
    local level = tonumber(record["Level"])
    local severity = level_to_severity[level]
    if severity == nil then
        -- Level 0 (LogAlways) や未知値は info 扱い
        severity = 6
    end
    record["SyslogSeverity"] = severity
    return 1, timestamp, record
end
