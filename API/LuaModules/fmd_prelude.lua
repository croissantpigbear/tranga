-- Tranga: compatibility layer for Free Manga Downloader 2 (FMD2) website modules.
-- Recreates the objects and helpers FMD2 gives its Lua modules (HTTP, CreateTXQuery, MANGAINFO, TASK, ...)
-- on top of a few plain functions provided by Tranga (__h_*). Loaded into a fresh Lua state for every call.

---------------------------------------------------------------------------------------------------
-- Sandbox: modules only get what FMD2 gives them (no files, no processes, no .NET)
---------------------------------------------------------------------------------------------------
luanet = nil
import = nil
CLRPackage = nil
io = nil
debug = nil
dofile = nil
loadfile = nil
do
	local o = os
	os = { time = o.time, date = o.date, clock = o.clock, difftime = o.difftime }
end
if package then
	package.loadlib = nil
	package.cpath = ''
	package.path = ''
end

---------------------------------------------------------------------------------------------------
-- Constants and simple globals
---------------------------------------------------------------------------------------------------
no_error = 0
net_problem = 1
information_not_found = 2
asUnknown = -1
asChecking = 0
asValid = 1
asInvalid = 2

function print(...)
	local t = {}
	for i = 1, select('#', ...) do t[#t + 1] = tostring((select(i, ...))) end
	__h_log(1, table.concat(t, ' '))
end

function sleep(ms) __h_sleep(tonumber(ms) or 0) end

local function str(v) if v == nil then return '' end return tostring(v) end

local function trim(s, chars)
	s = str(s)
	if chars and chars ~= '' then
		local set = '[' .. chars:gsub('[%]%[%^%-%%]', '%%%0') .. ']'
		return (s:gsub('^' .. set .. '+', ''):gsub(set .. '+$', ''))
	end
	return (s:gsub('^%s+', ''):gsub('%s+$', ''))
end
Trim = trim

function GetBetween(left, right, s)
	s = str(s)
	local a = s:find(str(left), 1, true)
	if not a then return '' end
	a = a + #str(left)
	local b = s:find(str(right), a, true)
	if not b then return '' end
	return s:sub(a, b - 1)
end

function SeparateLeft(s, d)
	s = str(s)
	local i = s:find(str(d), 1, true)
	if not i then return trim(s) end
	return trim(s:sub(1, i - 1))
end

function SeparateRight(s, d)
	s = str(s)
	local i = s:find(str(d), 1, true)
	if not i then return '' end
	return trim(s:sub(i + #str(d)))
end

function MaybeFillHost(host, url)
	host, url = str(host), str(url)
	if url == '' then return url end
	if url:match('^%a[%w+.-]*://') then return url end
	if url:sub(1, 2) == '//' then
		local scheme = host:match('^(%a[%w+.-]*):') or 'https'
		return scheme .. ':' .. url
	end
	host = host:gsub('/+$', '')
	if url:sub(1, 1) ~= '/' then url = '/' .. url end
	return host .. url
end

-- FMD2 status codes: '0' completed, '1' ongoing, '2' hiatus, '3' cancelled, '' unknown
function MangaInfoStatusIfPos(s, ongoing, completed, hiatus, cancelled)
	s = str(s):lower()
	if s == '' then return '' end
	local function any(list)
		list = str(list):lower()
		if list == '' then return false end
		for part in (list .. '|'):gmatch('(.-)|') do
			if part ~= '' and s:find(part, 1, true) then return true end
		end
		return false
	end
	if any(ongoing or 'ongoing') then return '1' end
	if any(completed or 'complete') then return '0' end
	if any(hiatus or 'hiatus') then return '2' end
	if any(cancelled or 'cancel') then return '3' end
	return ''
end

---------------------------------------------------------------------------------------------------
-- TStringList (0-based, .Count property, list[i] indexing, Values['name'])
---------------------------------------------------------------------------------------------------
local function valuesProxy(items, sepOf)
	return setmetatable({}, {
		__index = function(_, name)
			local prefix = str(name) .. sepOf()
			for _, v in ipairs(items) do
				if v:sub(1, #prefix) == prefix then return v:sub(#prefix + 1) end
			end
			return ''
		end,
		__newindex = function(_, name, value)
			local prefix = str(name) .. sepOf()
			for i, v in ipairs(items) do
				if v:sub(1, #prefix) == prefix then
					if value == nil or str(value) == '' then table.remove(items, i) else items[i] = prefix .. str(value) end
					return
				end
			end
			if value ~= nil and str(value) ~= '' then items[#items + 1] = prefix .. str(value) end
		end
	})
end

function TStringList()
	local items = {}
	local self = { __items = items, NameValueSeparator = '=', Delimiter = ',' }
	local m = {}
	function m.Add(s) items[#items + 1] = str(s); return #items - 1 end
	function m.AddText(s) for line in (str(s) .. '\n'):gmatch('(.-)\r?\n') do items[#items + 1] = line end end
	function m.Clear() for i = #items, 1, -1 do items[i] = nil end end
	function m.Reverse()
		local n = #items
		for i = 1, math.floor(n / 2) do items[i], items[n - i + 1] = items[n - i + 1], items[i] end
	end
	function m.Get(i) return items[(tonumber(i) or 0) + 1] or '' end
	function m.Set(i, v) items[(tonumber(i) or 0) + 1] = str(v) end
	function m.Delete(i) table.remove(items, (tonumber(i) or 0) + 1) end
	function m.IndexOf(s) for i, v in ipairs(items) do if v == str(s) then return i - 1 end end return -1 end
	function m.IndexOfName(s)
		local prefix = str(s) .. self.NameValueSeparator
		for i, v in ipairs(items) do if v:sub(1, #prefix) == prefix then return i - 1 end end
		return -1
	end
	function m.Sort() table.sort(items) end
	function m.GetCount() return #items end
	function m.SetText(s) m.Clear(); m.AddText(s) end
	function m.GetText() return #items == 0 and '' or (table.concat(items, '\r\n') .. '\r\n') end
	function m.Insert(i, s) table.insert(items, (tonumber(i) or 0) + 1, str(s)) end
	m.Strings = setmetatable({}, {
		__index = function(_, i) return items[(tonumber(i) or 0) + 1] or '' end,
		__newindex = function(_, i, v) items[(tonumber(i) or 0) + 1] = str(v) end
	})
	m.Values = valuesProxy(items, function() return rawget(self, 'NameValueSeparator') or '=' end)
	return setmetatable(self, {
		__index = function(_, k)
			if type(k) == 'number' then return items[k + 1] end
			if k == 'Count' then return #items end
			if k == 'Text' then return m.GetText() end
			if k == 'CommaText' or k == 'DelimitedText' then return table.concat(items, ',') end
			return m[k]
		end,
		__newindex = function(t, k, v)
			if type(k) == 'number' then items[k + 1] = str(v)
			elseif k == 'Text' then m.SetText(v)
			elseif k == 'CommaText' or k == 'DelimitedText' then
				m.Clear(); for part in (str(v) .. ','):gmatch('(.-),') do items[#items + 1] = part end
			else rawset(t, k, v) end
		end,
		__len = function() return #items end
	})
end

local function isList(v) return type(v) == 'table' and rawget(v, '__items') ~= nil end

---------------------------------------------------------------------------------------------------
-- JSON (utils.json)
---------------------------------------------------------------------------------------------------
local json = {}
do
	local escapes = { ['"'] = '\\"', ['\\'] = '\\\\', ['\b'] = '\\b', ['\f'] = '\\f', ['\n'] = '\\n', ['\r'] = '\\r', ['\t'] = '\\t' }
	local function encodeString(s)
		return '"' .. s:gsub('[%c"\\]', function(c) return escapes[c] or string.format('\\u%04x', c:byte()) end) .. '"'
	end
	local function isArray(t)
		local n = 0
		for k in pairs(t) do
			if type(k) ~= 'number' or k < 1 or math.floor(k) ~= k then return false end
			n = n + 1
		end
		for i = 1, n do if t[i] == nil then return false end end
		return true, n
	end
	local function encode(v, seen)
		local tv = type(v)
		if v == nil or v == json.null then return 'null' end
		if tv == 'boolean' then return tostring(v) end
		if tv == 'number' then
			if v ~= v or v == math.huge or v == -math.huge then return 'null' end
			if math.type and math.type(v) == 'integer' then return tostring(v) end
			if v == math.floor(v) and math.abs(v) < 1e15 then return string.format('%d', v) end
			return string.format('%.14g', v)
		end
		if tv == 'string' then return encodeString(v) end
		if tv == 'table' then
			if isList(v) then
				local out = {}
				for i = 0, v.Count - 1 do out[#out + 1] = encodeString(v[i]) end
				return '[' .. table.concat(out, ',') .. ']'
			end
			seen = seen or {}
			if seen[v] then error('circular reference in json.encode') end
			seen[v] = true
			local arr, n = isArray(v)
			local out = {}
			if arr and (n > 0 or getmetatable(v) ~= json.objectMeta) then
				for i = 1, n do out[i] = encode(v[i], seen) end
				seen[v] = nil
				return '[' .. table.concat(out, ',') .. ']'
			end
			local keys = {}
			for k in pairs(v) do keys[#keys + 1] = tostring(k) end
			table.sort(keys)
			for _, k in ipairs(keys) do
				local val = v[k]
				if val == nil then val = v[tonumber(k)] end
				out[#out + 1] = encodeString(k) .. ':' .. encode(val, seen)
			end
			seen[v] = nil
			return '{' .. table.concat(out, ',') .. '}'
		end
		error('cannot encode ' .. tv .. ' as json')
	end

	local function utf8char(c)
		if c < 0x80 then return string.char(c) end
		if c < 0x800 then return string.char(0xC0 | (c >> 6), 0x80 | (c & 0x3F)) end
		if c < 0x10000 then return string.char(0xE0 | (c >> 12), 0x80 | ((c >> 6) & 0x3F), 0x80 | (c & 0x3F)) end
		return string.char(0xF0 | (c >> 18), 0x80 | ((c >> 12) & 0x3F), 0x80 | ((c >> 6) & 0x3F), 0x80 | (c & 0x3F))
	end

	local function decode(s)
		local pos = 1
		local function ws() pos = s:find('[^ \t\r\n]', pos) or #s + 1 end
		local function err(msg) error('json.decode: ' .. msg .. ' at position ' .. pos) end
		local value
		local function str_()
			pos = pos + 1
			local buf = {}
			while true do
				local c = s:sub(pos, pos)
				if c == '' then err('unterminated string') end
				if c == '"' then pos = pos + 1; break end
				if c == '\\' then
					local e = s:sub(pos + 1, pos + 1)
					if e == 'u' then
						local code = tonumber(s:sub(pos + 2, pos + 5), 16) or 63
						pos = pos + 6
						if code >= 0xD800 and code <= 0xDBFF and s:sub(pos, pos + 1) == '\\u' then
							local low = tonumber(s:sub(pos + 2, pos + 5), 16) or 0
							code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00)
							pos = pos + 6
						end
						buf[#buf + 1] = utf8char(code)
					else
						local map = { b = '\b', f = '\f', n = '\n', r = '\r', t = '\t' }
						buf[#buf + 1] = map[e] or e
						pos = pos + 2
					end
				else
					local j = s:find('["\\]', pos)
					if not j then err('unterminated string') end
					buf[#buf + 1] = s:sub(pos, j - 1)
					pos = j
				end
			end
			return table.concat(buf)
		end
		function value()
			ws()
			local c = s:sub(pos, pos)
			if c == '{' then
				pos = pos + 1
				local t = setmetatable({}, json.objectMeta)
				ws()
				if s:sub(pos, pos) == '}' then pos = pos + 1; return t end
				while true do
					ws()
					if s:sub(pos, pos) ~= '"' then err('expected key') end
					local k = str_()
					ws()
					if s:sub(pos, pos) ~= ':' then err("expected ':'") end
					pos = pos + 1
					t[k] = value()
					ws()
					local d = s:sub(pos, pos)
					pos = pos + 1
					if d == '}' then return t end
					if d ~= ',' then err("expected ',' or '}'") end
				end
			elseif c == '[' then
				pos = pos + 1
				local t = {}
				ws()
				if s:sub(pos, pos) == ']' then pos = pos + 1; return t end
				while true do
					t[#t + 1] = value()
					ws()
					local d = s:sub(pos, pos)
					pos = pos + 1
					if d == ']' then return t end
					if d ~= ',' then err("expected ',' or ']'") end
				end
			elseif c == '"' then
				return str_()
			elseif s:sub(pos, pos + 3) == 'true' then pos = pos + 4; return true
			elseif s:sub(pos, pos + 4) == 'false' then pos = pos + 5; return false
			elseif s:sub(pos, pos + 3) == 'null' then pos = pos + 4; return nil
			else
				local num = s:match('^-?%d+%.?%d*[eE]?[-+]?%d*', pos)
				if not num or num == '' then err('unexpected character') end
				pos = pos + #num
				return math.tointeger(tonumber(num)) or tonumber(num)
			end
		end
		local ok, result = pcall(value)
		if not ok then error(result, 2) end
		return result
	end

	json.null = setmetatable({}, { __tostring = function() return 'null' end })
	json.objectMeta = {}
	json.encode = function(v) return encode(v) end
	json.decode = function(s) return decode(str(s)) end
end
__json = json

---------------------------------------------------------------------------------------------------
-- Streams (HTTP.Document)
---------------------------------------------------------------------------------------------------
local function newStream(text)
	local self = { __stream = true, __text = text or '' }
	local m = {}
	function m.ToString() return self.__text end
	function m.ReadString() return self.__text end
	function m.WriteString(s) self.__text = self.__text .. str(s) end
	function m.Clear() self.__text = '' end
	function m.SaveToFile() error('Saving files is not supported in Tranga') end
	function m.LoadFromFile() error('Loading files is not supported in Tranga') end
	return setmetatable(self, {
		__index = function(_, k) if k == 'Size' then return #self.__text end return m[k] end,
		__tostring = function() return self.__text end
	})
end
TMemoryStream = function() return newStream('') end

local function textOf(v)
	if type(v) == 'table' and rawget(v, '__stream') then return v.__text end
	if type(v) == 'table' and rawget(v, '__xq') then return v.ToString() end
	return str(v)
end

---------------------------------------------------------------------------------------------------
-- XPath / XQuery (CreateTXQuery) -- evaluated by Tranga (XPath 2.0 + FMD2-style JSON queries)
---------------------------------------------------------------------------------------------------
local function splitIds(s)
	local ids = {}
	for id in str(s):gmatch('%d+') do ids[#ids + 1] = tonumber(id) end
	return ids
end

-- A sequence of query results (FMD2's IXQValue)
local function newValue(ids)
	local self = { __xq = true, __ids = ids }
	local m = {}
	local function item(i) return ids[i] end
	function m.Get(i)
		if i == nil then
			local k = 0
			return function()
				k = k + 1
				if ids[k] then return newValue({ ids[k] }) end
			end
		end
		local id = ids[tonumber(i) or 1]
		return newValue(id and { id } or {})
	end
	function m.ToString()
		if #ids == 0 then return '' end
		if #ids == 1 then return __h_str(ids[1]) end
		local t = {}
		for _, id in ipairs(ids) do t[#t + 1] = __h_str(id) end
		return table.concat(t, ' ')
	end
	function m.GetAttribute(name) return item(1) and __h_attr(item(1), str(name)) or '' end
	function m.GetProperty(name)
		local id = item(1) and __h_prop(item(1), str(name)) or 0
		return newValue((id and id ~= 0) and { id } or {})
	end
	function m.InnerHTML() return item(1) and __h_html(item(1), 0) or '' end
	function m.OuterHTML() return item(1) and __h_html(item(1), 1) or '' end
	function m.InnerText() return item(1) and __h_html(item(1), 2) or '' end
	return setmetatable(self, {
		__index = function(_, k)
			if k == 'Count' then return #ids end
			return m[k]
		end,
		__tostring = function() return m.ToString() end,
		__len = function() return #ids end
	})
end

local function ctxId(v)
	if type(v) == 'table' and rawget(v, '__xq') then return rawget(v, '__ids')[1] or 0 end
	return 0
end

function CreateTXQuery(source)
	local doc = __h_doc(source ~= nil and textOf(source) or '')
	local x = {}
	local function eval(expr, ctx)
		local r = __h_q(doc, ctxId(ctx), str(expr))
		if r == '!' then error('Tranga stopped this module: it took too long', 0) end
		return splitIds(r)
	end
	function x.ParseHTML(s) doc = __h_doc(textOf(s)) end
	function x.XPath(expr, ctx) return newValue(eval(expr, ctx)) end
	function x.XPathString(expr, ctx) return newValue(eval(expr, ctx)).ToString() end
	function x.XPathCount(expr, ctx) return #eval(expr, ctx) end
	-- (expr) / (expr, separator) / (expr, separator, ctx) / (expr, list) / (expr, list, ctx) / (expr, {exclude}, separator, ctx)
	function x.XPathStringAll(expr, a, b, c)
		local list, sep, ctx, exclude = nil, ', ', nil, nil
		if isList(a) then list, ctx = a, b
		elseif type(a) == 'table' and not rawget(a, '__xq') then exclude, sep, ctx = a, b or ', ', c
		elseif type(a) == 'string' then sep, ctx = a, b
		elseif type(a) == 'table' then ctx = a end
		local ids = eval(expr, ctx)
		if list then
			for _, id in ipairs(ids) do list.Add(trim(__h_str(id))) end
			return
		end
		local out = {}
		for _, id in ipairs(ids) do
			local s = trim(__h_str(id))
			local skip = false
			if exclude then for _, e in ipairs(exclude) do if s:lower() == str(e):lower() then skip = true end end end
			if s ~= '' and not skip then out[#out + 1] = s end
		end
		return table.concat(out, sep)
	end
	function x.XPathHREFAll(expr, links, names, ctx)
		for _, id in ipairs(eval(expr, ctx)) do
			links.Add(__h_attr(id, 'href'))
			names.Add(trim(__h_str(id)))
		end
	end
	function x.XPathHREFTitleAll(expr, links, titles, ctx)
		for _, id in ipairs(eval(expr, ctx)) do
			links.Add(__h_attr(id, 'href'))
			titles.Add(__h_attr(id, 'title'))
		end
	end
	return x
end

---------------------------------------------------------------------------------------------------
-- HTTP (THTTPSendThread)
---------------------------------------------------------------------------------------------------
local function headerBlock(list)
	local out = {}
	for i = 0, list.Count - 1 do
		local line = list[i]
		local name, value = line:match('^([^=:]+)[=:]%s*(.*)$')
		if name and value ~= '' then out[#out + 1] = trim(name) .. ': ' .. value end
	end
	return table.concat(out, '\n')
end

function NewHTTP()
	local self = {
		ResultCode = 0, ResultString = '', MimeType = '', UserAgent = '', RetryCount = 1, LastURL = '',
		EnabledCookies = true, AllowServerErrorResponse = true, Terminated = false, FollowRedirection = true,
		Headers = TStringList(), Cookies = TStringList(), ResponseHeaders = TStringList(), Document = newStream('')
	}
	self.Headers.NameValueSeparator = ':'
	local function request(method, url, data)
		local headers = headerBlock(self.Headers)
		local cookies = {}
		for i = 0, self.Cookies.Count - 1 do cookies[#cookies + 1] = self.Cookies[i] end
		if #cookies > 0 then headers = headers .. '\nCookie: ' .. table.concat(cookies, '; ') end
		local tries = math.max(1, (tonumber(self.RetryCount) or 1))
		local code = 0
		for _ = 1, math.min(tries, 3) do
			code = __h_http(method, MaybeFillHost(MODULE and MODULE.RootURL or '', str(url)), data and textOf(data) or '', headers, str(self.MimeType), str(self.UserAgent))
			if code == -2 then error('Tranga stopped this module: it took too long or made too many requests', 0) end
			if code > 0 and code < 500 then break end
		end
		self.ResultCode = code
		self.LastURL = __h_lasturl()
		self.Document = newStream(__h_body())
		self.ResponseHeaders.SetText(__h_resheaders())
		return self.Document.Size > 0 or (code >= 200 and code < 400)
	end
	function self.GET(url) return request('GET', url) end
	function self.POST(url, data) return request('POST', url, data or '') end
	function self.HEAD(url) return request('HEAD', url) end
	function self.XHR(url)
		self.Headers.Values['X-Requested-With'] = 'XMLHttpRequest'
		return request('GET', url)
	end
	function self.Request(method, url, data) return request(str(method):upper(), url, data) end
	function self.Reset()
		self.Headers.Clear(); self.MimeType = ''; self.Document = newStream(''); self.ResultCode = 0
	end
	self.ResetBasic = self.Reset
	function self.ClearCookies() self.Cookies.Clear() end
	self.ClearCookiesStorage = self.ClearCookies
	function self.GetCookies()
		local t = {}
		for i = 0, self.Cookies.Count - 1 do t[#t + 1] = self.Cookies[i] end
		return table.concat(t, '; ')
	end
	function self.AddServerCookies(_, cookieString)
		for part in (str(cookieString) .. ';'):gmatch('(.-);') do
			local name, value = part:match('^%s*([^=]+)=(.*)$')
			if name then self.Cookies.Values[trim(name)] = trim(value) end
		end
	end
	self.ParseServerCookies = self.AddServerCookies
	function self.SetProxy() end
	return self
end

---------------------------------------------------------------------------------------------------
-- Website modules (NewWebsiteModule / MODULE)
---------------------------------------------------------------------------------------------------
__modules = {}

function NewWebsiteModule()
	local m = {
		ID = '', Name = '', RootURL = '', Category = '', TotalDirectory = 1, CurrentDirectoryIndex = 0,
		MaxTaskLimit = 0, MaxThreadPerTaskLimit = 0, MaxConnectionLimit = 0, ActiveTaskCount = 0, ActiveConnectionCount = 0,
		SortedList = false, InformationAvailable = true, FavoriteAvailable = true, DynamicPageLink = false,
		AccountSupport = false, LastUpdated = '', Tag = 0, __options = {}
	}
	-- Kept between calls and restarts (FMD2 keeps it while the program runs)
	m.Storage = setmetatable({}, {
		__index = function(_, k) return __h_sget(str(m.ID), str(k)) end,
		__newindex = function(_, k, v) __h_sset(str(m.ID), str(k), v == nil and '' or tostring(v)) end
	})
	m.Account = { Enabled = false, Username = '', Password = '', Status = asUnknown, Cookies = '' }
	local function addOption(name, _, default) m.__options[str(name)] = default end
	m.AddOptionCheckBox = addOption
	m.AddOptionEdit = addOption
	m.AddOptionSpinEdit = addOption
	function m.AddOptionComboBox(name, caption, _, default) addOption(name, caption, default) end
	function m.GetOption(name) return m.__options[str(name)] end
	function m.AddServerCookies(domain, cookies) if HTTP then HTTP.AddServerCookies(domain, cookies) end end
	function m.GetServerCookies() return HTTP and HTTP.GetCookies() or '' end
	function m.RemoveCookies() end
	function m.ClearCookies() if HTTP then HTTP.ClearCookies() end end
	__modules[#__modules + 1] = m
	return m
end

---------------------------------------------------------------------------------------------------
-- require: templates.* / utils.* files uploaded to Tranga, and FMD2's built-in fmd.* libraries
---------------------------------------------------------------------------------------------------
local function unsupported(name)
	return setmetatable({}, {
		__index = function(_, k)
			return function() error(name .. '.' .. tostring(k) .. ' is not supported in Tranga (FMD2 only)', 2) end
		end
	})
end

local crypto = {}
for fn, op in pairs({
	EncodeBase64 = 'b64e', DecodeBase64 = 'b64d', EncodeBase64URL = 'b64ue', DecodeBase64URL = 'b64ud',
	EncodeURL = 'urle', EncodeURLElement = 'urlelem', DecodeURL = 'urld',
	HTMLEncode = 'htmle', HTMLDecode = 'htmld', MD5Hex = 'md5', SHA1Hex = 'sha1', SHA256Hex = 'sha256',
	SHA512Hex = 'sha512', StrToHexStr = 'hex', HexToStr = 'unhex'
}) do
	crypto[fn] = function(s, extra) return __h_crypto(op, str(s), str(extra)) end
end
setmetatable(crypto, { __index = function(_, k)
	return function() error('fmd.crypto.' .. tostring(k) .. ' is not supported in Tranga', 2) end
end })

local builtins = {
	['fmd.crypto'] = crypto,
	['fmd.env'] = { SelectedLanguage = 'en', LuaDirectory = '', Version = 'Tranga' },
	['fmd.logger'] = {
		Send = function(...) print(...) end,
		SendError = function(...) __h_log(3, table.concat({ ... }, ' ')) end,
		SendWarning = function(...) __h_log(2, table.concat({ ... }, ' ')) end,
		SendException = function(...) __h_log(3, table.concat({ ... }, ' ')) end
	},
	['fmd.fileutil'] = {
		ExtractFileNameOnly = function(p) return (str(p):match('([^/\\]+)$') or ''):gsub('%.[^.]*$', '') end,
		ExtractFileExt = function(p) return str(p):match('(%.[^./\\]*)$') or '' end
	},
	['utils.json'] = json,
	['fmd.duktape'] = unsupported('fmd.duktape'),
	['fmd.subprocess'] = unsupported('fmd.subprocess'),
	['utils.nodejs'] = unsupported('utils.nodejs'),
	['fmd.mangafoxwatermark'] = unsupported('fmd.mangafoxwatermark'),
	['fmd.imagepuzzle'] = unsupported('fmd.imagepuzzle'),
	['fmd.pcre2'] = unsupported('fmd.pcre2'),
	['fmd.gzip'] = unsupported('fmd.gzip'),
	['fmd.websitebypass'] = unsupported('fmd.websitebypass'),
}

local loaded = {}
function require(name)
	name = str(name)
	if loaded[name] ~= nil then return loaded[name] end
	-- Uploaded files win (e.g. a newer utils.json), then built-ins
	local source = __h_require(name)
	local result
	if source ~= nil and source ~= '' then
		source = source:gsub('^\239\187\191', '') -- UTF-8 byte order mark
		local chunk, err = load(source, '@' .. name .. '.lua', 't')
		if not chunk then error('Could not load ' .. name .. ': ' .. tostring(err), 2) end
		result = chunk(name)
		if result == nil then result = true end
	elseif builtins[name] then
		result = builtins[name]
	elseif name:match('^templates%.') then
		error('This module needs ' .. name:gsub('^templates%.', '') .. '.lua from FMD2\'s lua/templates folder. Upload it as a template.', 2)
	elseif name:match('^utils%.') then
		error('This module needs ' .. name:gsub('^utils%.', '') .. '.lua from FMD2\'s lua/utils folder. Upload it as a helper.', 2)
	else
		error(name .. ' is not available in Tranga', 2)
	end
	loaded[name] = result
	return result
end

---------------------------------------------------------------------------------------------------
-- Entry points called by Tranga. Each returns a JSON string.
---------------------------------------------------------------------------------------------------
local function moduleById(id)
	for _, m in ipairs(__modules) do if m.ID == id then return m end end
	return nil
end

local function listToTable(list)
	local t = {}
	for i = 0, list.Count - 1 do t[#t + 1] = list[i] end
	return t
end

-- Runs the module's Init() once per Lua state (every call starts with a fresh state)
local initDone = false
local function ensureInit()
	if initDone then return end
	initDone = true
	if type(Init) == 'function' then
		local r = Init()
		if type(r) == 'table' and r.ID and not moduleById(r.ID) then __modules[#__modules + 1] = r end
	end
end

local function prepare(id)
	ensureInit()
	local m = moduleById(id)
	if not m then error('Module ' .. tostring(id) .. ' not found (Init did not register it)') end
	MODULE = m
	HTTP = NewHTTP()
	return m
end

local function handler(name)
	local fname = MODULE[name]
	if fname == nil or fname == '' then return nil end
	local f = _G[fname]
	if type(f) ~= 'function' then error(name .. ' points to ' .. tostring(fname) .. ', which is not a function') end
	return f
end

-- Return value of a handler as success (FMD2 treats any non-false value from page handlers as success;
-- GetInfo / GetNameAndLink return no_error (0) on success)
local function codeOf(v)
	if v == nil then return no_error end
	if v == true then return no_error end
	if v == false then return net_problem end
	return tonumber(v) or no_error
end

function __fmd_init()
	ensureInit()
	local out = {}
	for _, m in ipairs(__modules) do
		local options = setmetatable({}, json.objectMeta)
		for k, v in pairs(m.__options) do options[k] = str(v) end
		out[#out + 1] = {
			id = str(m.ID), name = str(m.Name), rootUrl = str(m.RootURL), category = str(m.Category),
			totalDirectory = tonumber(m.TotalDirectory) or 1, accountSupport = m.AccountSupport and true or false,
			onGetInfo = str(m.OnGetInfo), onGetPageNumber = str(m.OnGetPageNumber), onGetImageURL = str(m.OnGetImageURL),
			onGetNameAndLink = str(m.OnGetNameAndLink), onGetDirectoryPageNumber = str(m.OnGetDirectoryPageNumber),
			onBeforeDownloadImage = str(m.OnBeforeDownloadImage), onDownloadImage = str(m.OnDownloadImage),
			onLogin = str(m.OnLogin), onTaskStart = str(m.OnTaskStart), options = options
		}
	end
	return json.encode(out)
end

function __fmd_info(id, url)
	prepare(id)
	URL = str(url)
	MANGAINFO = {
		URL = MaybeFillHost(MODULE.RootURL, url), Title = '', AltTitles = '', Link = '', CoverLink = '', Authors = '', Artists = '',
		Genres = '', Status = '', Summary = '', ChapterLinks = TStringList(), ChapterNames = TStringList()
	}
	local f = handler('OnGetInfo')
	if not f then error('This module has no OnGetInfo, so it cannot read manga pages') end
	local code = codeOf(f())
	return json.encode({
		code = code, url = MANGAINFO.URL, title = str(MANGAINFO.Title), altTitles = str(MANGAINFO.AltTitles),
		coverLink = str(MANGAINFO.CoverLink), authors = str(MANGAINFO.Authors), artists = str(MANGAINFO.Artists),
		genres = str(MANGAINFO.Genres), status = str(MANGAINFO.Status), summary = str(MANGAINFO.Summary),
		chapterLinks = listToTable(MANGAINFO.ChapterLinks), chapterNames = listToTable(MANGAINFO.ChapterNames)
	})
end

local function newTask(url)
	TASK = {
		PageLinks = TStringList(), PageContainerLinks = TStringList(), FileNames = TStringList(),
		ChapterLinks = TStringList(), ChapterNames = TStringList(), PageNumber = 0, Link = str(url),
		CurrentDownloadChapterPtr = 0, CurrentMaxFileNameLength = 255
	}
	TASK.ChapterLinks.Add(url)
end

function __fmd_pages(id, url)
	prepare(id)
	URL = str(url)
	newTask(url)
	local start = handler('OnTaskStart')
	if start and start() == false then return json.encode({ ok = false, error = 'TaskStart refused' }) end
	local f = handler('OnGetPageNumber')
	if not f then error('This module has no OnGetPageNumber, so it cannot download chapters') end
	local result = f()
	local ok = result ~= false and result ~= nil or TASK.PageLinks.Count > 0 or TASK.PageContainerLinks.Count > 0
	local pages = listToTable(TASK.PageLinks)
	local containers = listToTable(TASK.PageContainerLinks)
	-- Pages that sit on their own page: ask GetImageURL for each
	local imageUrl = handler('OnGetImageURL')
	if imageUrl and (#containers > 0 or (#pages == 0 and (tonumber(TASK.PageNumber) or 0) > 0)) then
		local n = #containers > 0 and #containers or tonumber(TASK.PageNumber)
		for i = 0, n - 1 do
			if TASK.PageLinks.Count <= i then TASK.PageLinks.Add('') end
		end
		for i = 0, n - 1 do
			WORKID = i
			URL = MaybeFillHost(MODULE.RootURL, containers[i + 1] or str(url))
			HTTP = NewHTTP()
			imageUrl()
		end
		pages = listToTable(TASK.PageLinks)
	end
	for i, p in ipairs(pages) do pages[i] = MaybeFillHost(MODULE.RootURL, p) end
	return json.encode({ ok = ok and true or false, pages = pages, containers = containers })
end

function __fmd_beforedownload(id, url, chapterUrl)
	prepare(id)
	URL = str(url)
	WORKID = 0
	newTask(MaybeFillHost(MODULE.RootURL, chapterUrl))
	local f = handler('OnBeforeDownloadImage')
	local ok = true
	if f then ok = f() ~= false end
	local headers = setmetatable({}, json.objectMeta)
	for i = 0, HTTP.Headers.Count - 1 do
		local name, value = HTTP.Headers[i]:match('^([^=:]+)[=:]%s*(.*)$')
		if name then headers[trim(name)] = value end
	end
	local cookies = HTTP.GetCookies()
	if cookies ~= '' then headers['Cookie'] = cookies end
	if HTTP.UserAgent ~= '' then headers['User-Agent'] = HTTP.UserAgent end
	return json.encode({ ok = ok, url = str(URL), headers = headers })
end

-- Reads the whole manga list (FMD2's "update list"), up to maxPages pages per directory
function __fmd_directory(id, maxPages)
	prepare(id)
	maxPages = tonumber(maxPages) or 200
	local links, names = TStringList(), TStringList()
	local before = handler('OnBeforeUpdateList')
	if before then UPDATELIST = { CurrentDirectoryPageNumber = 0, UpdateStatusText = function() end }; before() end
	local nameAndLink = handler('OnGetNameAndLink')
	if not nameAndLink then error('This module has no OnGetNameAndLink, so it cannot list titles (paste links instead)') end
	local pageNumber = handler('OnGetDirectoryPageNumber')
	local dirs = math.max(1, tonumber(MODULE.TotalDirectory) or 1)
	local pagesRead, errors = 0, 0
	for dir = 0, dirs - 1 do
		MODULE.CurrentDirectoryIndex = dir
		PAGENUMBER = 1
		UPDATELIST = { CurrentDirectoryPageNumber = 1, UpdateStatusText = function() end }
		if pageNumber then
			HTTP = NewHTTP()
			URL = ''
			if codeOf(pageNumber()) ~= no_error then errors = errors + 1 end
			PAGENUMBER = tonumber(PAGENUMBER) or 1
			UPDATELIST.CurrentDirectoryPageNumber = PAGENUMBER
		end
		local total = math.max(1, tonumber(PAGENUMBER) or 1)
		local page = 0
		while page < total and pagesRead < maxPages do
			HTTP = NewHTTP()
			LINKS, NAMES = TStringList(), TStringList()
			URL = page
			UPDATELIST.CurrentDirectoryPageNumber = total
			local code = codeOf(nameAndLink())
			pagesRead = pagesRead + 1
			if code ~= no_error then errors = errors + 1; break end
			for i = 0, LINKS.Count - 1 do
				links.Add(MaybeFillHost(MODULE.RootURL, LINKS[i]))
				names.Add(trim(NAMES[i] or ''))
			end
			-- Modules without GetDirectoryPageNumber ask for more pages by raising CurrentDirectoryPageNumber
			local wanted = tonumber(UPDATELIST.CurrentDirectoryPageNumber) or total
			if wanted > total then total = wanted end
			if LINKS.Count == 0 then break end
			page = page + 1
		end
	end
	return json.encode({ links = listToTable(links), names = listToTable(names), pagesRead = pagesRead, errors = errors })
end
