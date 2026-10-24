-- BEGIN MotionVfxLive MDF-XL compatibility v1
-- This prefix is installed into MDF-XL.lua, not loaded as an autorun script.
-- Only this chunk's local `re` is adapted. Other mods and the global API retain
-- their original callbacks and error handling. No MDF settings are changed.
local re = (function(original)
    if type(original) ~= "table" or type(original.on_frame) ~= "function" then
        return original
    end
    local status = {
        version = "1.0.0", registeredCallbacks = 0, callbackFrames = 0,
        errorCount = 0, consecutiveErrors = 0, lastError = nil, lastErrorTime = nil
    }
    rawset(_G, "MotionVfxMdfxlCompatibility", status)
    local original_on_frame = original.on_frame
    local pack, unpack_values = table.pack, table.unpack
    local protected_call = xpcall
    local protected_log = pcall
    local traceback = debug and debug.traceback
    local get_time = os and os.time
    local function now()
        if type(get_time) == "function" then
            local ok, value = protected_log(get_time)
            if ok and type(value) == "number" then return value end
        end
        -- Without a clock, emit only the first error instead of flooding disk.
        return 0
    end
    local function describe(err)
        local ok, message = protected_log(tostring, err)
        if not ok then message = "MDF-XL frame callback raised an unreadable error" end
        if type(traceback) == "function" then
            local traced, details = protected_log(traceback, message, 2)
            if traced and type(details) == "string" then return details end
        end
        return message
    end
    local proxy = setmetatable({}, { __index = original })
    function proxy.on_frame(callback)
        -- Keep REFramework's registration validation for invalid arguments.
        if type(callback) ~= "function" then return original_on_frame(callback) end
        local last_log_time = nil
        local result = pack(original_on_frame(function(...)
            status.callbackFrames = status.callbackFrames + 1
            local values = pack(protected_call(callback, describe, ...))
            if values[1] then
                status.consecutiveErrors = 0
                return unpack_values(values, 2, values.n)
            end
            status.errorCount = status.errorCount + 1
            status.consecutiveErrors = status.consecutiveErrors + 1
            status.lastError, status.lastErrorTime = values[2], now()
            local stamp = status.lastErrorTime
            if last_log_time == nil or stamp < last_log_time or stamp - last_log_time >= 10 then
                last_log_time = stamp
                local logger = log and (log.error or log.warn)
                if type(logger) == "function" then
                    protected_log(logger, "[MotionVfxLive MDF-XL compatibility] MDF-XL update failed; " ..
                        "later scripts will continue. Retry on next frame.\n" .. status.lastError)
                end
            end
            -- A failure must not escape to ScriptState::on_frame's shared loop,
            -- nor return a STOP token. Retry MDF-XL normally on the next frame.
            return nil
        end))
        status.registeredCallbacks = status.registeredCallbacks + 1
        if log and type(log.info) == "function" then
            protected_log(log.info, "[MotionVfxLive MDF-XL compatibility] 1.0.0 active for MDF-XL on_frame.")
        end
        return unpack_values(result, 1, result.n)
    end
    return proxy
end)(re)
-- END MotionVfxLive MDF-XL compatibility v1
