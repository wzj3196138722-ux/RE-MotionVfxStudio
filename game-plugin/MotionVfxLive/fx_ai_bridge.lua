-- File transport only. The injected host runs every operation through the
-- editor on its existing REFramework frame thread; this module has no SDK API.
local M = { version = "1.0.1", protocol = 1 }
local request_path, response_path = "MotionVfxLive/ai-request.json", "MotionVfxLive/ai-response.json"
local journal_path = "MotionVfxLive/ai-journal.json"
local function valid_id(id) return type(id) == "string" and #id > 0 and #id <= 128 and id:match("^[%w_%-]+$") ~= nil end
local function fingerprint(request)
    local nodes, seen = 0, {}
    local function encode(value, depth)
        nodes = nodes + 1
        if nodes > 1024 or depth > 8 then error("Request is too complex") end
        local kind = type(value)
        if kind == "nil" then return "null" end
        if kind == "boolean" then return tostring(value) end
        if kind == "number" then
            if value ~= value or value == math.huge or value == -math.huge then error("Non-finite request number") end
            return string.format("%.17g", value)
        end
        if kind == "string" then
            if #value > 32768 then error("Request string is too long") end
            return string.format("%q", value)
        end
        if kind ~= "table" or seen[value] then error("Invalid request value") end
        seen[value] = true
        local keys, parts = {}, {}
        for key in pairs(value) do
            if type(key) ~= "string" and type(key) ~= "number" then error("Invalid request key") end
            keys[#keys + 1] = key
        end
        table.sort(keys, function(a, b) return type(a) .. tostring(a) < type(b) .. tostring(b) end)
        for _, key in ipairs(keys) do parts[#parts + 1] = encode(key, depth + 1) .. ":" .. encode(value[key], depth + 1) end
        seen[value] = nil
        return "{" .. table.concat(parts, ",") .. "}"
    end
    -- Expiry may change on transport retries; identity is the requested action.
    local result = encode({ action = request.action, arguments = request.arguments }, 0)
    if #result > 65536 then error("Request is too large") end
    return result
end
local function clone(value, seen, depth)
    if type(value) ~= "table" then return value end
    if depth > 32 or seen[value] then error("Invalid response object graph") end
    seen[value] = true
    local result = {}; for key, item in pairs(value) do result[key] = clone(item, seen, depth + 1) end
    seen[value] = nil
    return result
end
function M.new(host)
    local self = { ready = true, active = nil, cache = {}, cache_order = {}, entries = {}, by_id = {}, last_read = nil }
    local last_transport_read
    local function transport_due()
        -- Only disk polling is throttled. An active operation's waiter and its
        -- completion/timeout processing still run on every game frame.
        if type(host.clock) ~= "function" then return true end
        local ok, now = pcall(host.clock)
        if not ok or type(now) ~= "number" or now ~= now or now == math.huge or now == -math.huge then
            last_transport_read = nil
            return true
        end
        if last_transport_read and now >= last_transport_read and now - last_transport_read < 0.1 then return false end
        last_transport_read = now
        return true
    end
    local function write(path, value)
        local ok, result = pcall(host.write, path, value)
        return ok and result ~= false
    end
    local function failure(id, code, message, uncertain)
        return { protocol = 1, id = id, ok = false, error = { code = code, message = tostring(message),
            uncertain = uncertain or nil, requestId = uncertain and id or nil } }
    end
    local function emit(response)
        if not write(response_path, response) then self.error = "AI response could not be written"; return false end
        return true
    end
    local function persist()
        local snapshot = clone({ protocol = 1, entries = self.entries }, {}, 0)
        if not write(journal_path, snapshot) then return false end
        -- REFramework's JSON writer may return true even when its stream
        -- failed. Never authorize execution without reading this receipt back.
        local ok, actual = pcall(host.read, journal_path)
        if not ok or type(actual) ~= "table" or actual.protocol ~= 1 or type(actual.entries) ~= "table" or #actual.entries ~= #self.entries then return false end
        local count = 0
        for key in pairs(actual.entries) do
            if type(key) ~= "number" or key % 1 ~= 0 or key < 1 or key > #self.entries then return false end
            count = count + 1
        end
        if count ~= #self.entries then return false end
        for index, expected in ipairs(self.entries) do
            local entry = actual.entries[index]
            if type(entry) ~= "table" or entry.id ~= expected.id or entry.fingerprint ~= expected.fingerprint or
                entry.state ~= expected.state or entry.ok ~= expected.ok then return false end
        end
        return true
    end
    local function remember(response)
        if not self.cache[response.id] then self.cache_order[#self.cache_order + 1] = response.id end
        self.cache[response.id] = response
        while #self.cache_order > 8 do self.cache[table.remove(self.cache_order, 1)] = nil end
    end
    local function complete(id, ok, payload)
        if not self.active or self.active.id ~= id then return end
        local response = ok and { protocol = 1, id = id, ok = true, result = payload } or
            failure(id, type(payload) == "table" and payload.code or "operation_failed",
                type(payload) == "table" and payload.message or payload, type(payload) == "table" and payload.uncertain)
        local copied, snapshot = pcall(clone, response, {}, 0)
        if copied then response = snapshot else response = failure(id, "response_invalid", snapshot, true) end
        local entry = self.by_id[id]
        entry.state, entry.ok = "completed", response.ok
        -- An accepted journal entry is already durable. If completion cannot
        -- be persisted, the next session reports uncertain and never replays it.
        if not persist() then self.ready, self.error = false, "AI completion journal could not be verified; no further operation will execute" end
        self.active = nil; host.busy(false)
        remember(response); emit(response)
    end
    local exists_ok, exists = pcall(host.exists, journal_path)
    local loaded, journal = pcall(host.read, journal_path)
    if not exists_ok or type(exists) ~= "boolean" or not loaded or exists and journal == nil or
        journal ~= nil and (type(journal) ~= "table" or journal.protocol ~= 1 or type(journal.entries) ~= "table") then
        self.ready, self.error = false, "AI journal is unreadable; operations were disabled to prevent replay"
    elseif journal then
        local count = 0
        for key in pairs(journal.entries) do
            if type(key) ~= "number" or key % 1 ~= 0 or key < 1 or key > #journal.entries then self.ready = false end
            count = count + 1
        end
        if count ~= #journal.entries then self.ready = false end
        if not self.ready then self.error = "AI journal must contain a contiguous entry array; operations were disabled" end
        for _, entry in ipairs(journal.entries) do
            if type(entry) ~= "table" or not valid_id(entry.id) or type(entry.fingerprint) ~= "string" or self.by_id[entry.id] or
                (entry.state ~= "accepted" and entry.state ~= "completed") or #self.entries >= 8192 then
                self.ready, self.error = false, "AI journal is invalid; operations were disabled to prevent replay"; break
            end
            self.entries[#self.entries + 1], self.by_id[entry.id] = entry, entry
        end
    end
    -- A request already present when the editor starts is never executed.
    local startup_ok, startup = pcall(host.read, request_path)
    if startup_ok and type(startup) == "table" and valid_id(startup.id) then
        self.last_read = startup.id
        if self.ready and not self.by_id[startup.id] then
            local ok, value = pcall(fingerprint, startup)
            if ok and #self.entries < 8192 then
                local entry = { id = startup.id, fingerprint = value, state = "completed", ok = false }
                self.entries[#self.entries + 1], self.by_id[startup.id] = entry, entry
                if not persist() then self.ready, self.error = false, "AI startup journal could not be written" end
            end
        end
        local response_ok, previous = pcall(host.read, response_path)
        if not response_ok or type(previous) ~= "table" or previous.id ~= startup.id then
            local response = failure(startup.id, "editor_reloaded", "The editor restarted; this existing request was not replayed. Check status before continuing.", true)
            remember(response); emit(response)
        end
    end
    function self:tick()
        if self.active and self.active.waiter then
            local waiting_id = self.active.id
            local ok, err = pcall(self.active.waiter)
            if not ok then complete(waiting_id, false, { code = "operation_failed", message = tostring(err), uncertain = true }) end
        end
        if self.active and not self.active.timeout_reported and host.now() - self.active.started > 120 then
            self.active.timeout_reported = true
            emit(failure(self.active.id, "operation_uncertain", "Operation is still pending and was not repeated. Use the in-game editor to inspect or restore; the AI channel remains busy until it settles.", true))
        end
        if not transport_due() then return end
        local ok, request = pcall(host.read, request_path)
        if not ok or type(request) ~= "table" or not valid_id(request.id) then return end
        local encoded, value = pcall(fingerprint, request)
        if not encoded then
            if self.last_read ~= request.id then emit(failure(request.id, "invalid_request", value)); self.last_read = request.id end
            return
        end
        local known = self.by_id[request.id]
        if known then
            if known.fingerprint ~= value then emit(failure(request.id, "id_conflict", "This ID already belongs to different arguments")); return end
            if self.active and self.active.id == request.id then return end
            if self.last_read ~= request.id then
                emit(self.cache[request.id] or failure(request.id, known.state == "accepted" and "operation_uncertain" or "already_processed",
                    "This request ID was already recorded and will not execute again. Inspect current status.", known.state == "accepted"))
                self.last_read = request.id
            end
            return
        end
        if self.last_read == request.id then return end
        self.last_read = request.id
        if not self.ready then emit(failure(request.id, "journal_unavailable", self.error)); return end
        if #self.entries >= 8192 then emit(failure(request.id, "journal_full", "AI request journal is full; no operation was executed")); return end
        local entry = { id = request.id, fingerprint = value, state = "accepted" }
        self.entries[#self.entries + 1], self.by_id[request.id] = entry, entry
        if not persist() then
            self.ready, self.error = false, "AI request could not be journaled; no operation was executed"
            emit(failure(request.id, "journal_unavailable", self.error)); return
        end
        local reject
        if request.protocol ~= 1 or type(request.action) ~= "string" or type(request.arguments) ~= "table" then
            reject = { code = "invalid_request", message = "Expected protocol 1, an action, and an arguments object" }
        elseif type(request.expiresAtUnixSeconds) ~= "number" or request.expiresAtUnixSeconds % 1 ~= 0 or
            request.expiresAtUnixSeconds == math.huge or request.expiresAtUnixSeconds > host.now() + 3600 then
            reject = { code = "invalid_request", message = "A finite near-term expiresAtUnixSeconds is required" }
        elseif request.expiresAtUnixSeconds <= host.now() then
            reject = { code = "expired", message = "Request expired before the editor accepted it" }
        elseif self.active then reject = { code = "busy", message = "Another AI operation is still pending" } end
        if reject then
            entry.state, entry.ok = "completed", false; persist()
            local response = failure(request.id, reject.code, reject.message); remember(response); emit(response); return
        end
        self.active = { id = request.id, action = request.action, started = host.now() }
        host.busy(true, request.id, request.action)
        local done = function(success, payload) complete(request.id, success, payload) end
        local started, waiter = pcall(host.start, request.action, request.arguments, done)
        if not started then done(false, { code = "operation_failed", message = tostring(waiter), uncertain = true })
        elseif self.active and self.active.id == request.id and type(waiter) == "function" then self.active.waiter = waiter end
    end
    function self:shutdown()
        self.ready = false
        if self.active then complete(self.active.id, false, { code = "editor_reloaded", message = "Editor stopped while operation was pending; inspect current state before continuing.", uncertain = true }) end
    end
    return self
end
return M
