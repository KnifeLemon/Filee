--- Yazi plugin to convert selected or hovered files with Filee offline converter

local get_targets = ya.sync(function()
	local tab = cx.active
	local paths = {}

	-- Check for selected files
	if tab.selected then
		for _, url in pairs(tab.selected) do
			table.insert(paths, tostring(url))
		end
	end

	-- Fallback to currently hovered file if no explicit selection
	if #paths == 0 and tab.current and tab.current.hovered then
		table.insert(paths, tostring(tab.current.hovered.url))
	end

	return paths
end)

local function is_successful(status)
	if not status then
		return false
	end
	if type(status.success) == "function" then
		return status:success()
	end
	if status.success == true then
		return true
	end
	if type(status.code) == "function" then
		return status:code() == 0
	end
	if status.code == 0 then
		return true
	end
	return false
end

local function notify(title, content, level)
	ya.notify({
		title = title,
		content = content,
		level = level or "info",
		timeout = 4,
	})
	-- Best-effort freedesktop desktop notification if notify-send exists
	Command("notify-send")
		:arg(title)
		:arg(content)
		:spawn()
end

local M = {}

function M:entry()
	local targets = get_targets()
	if #targets == 0 then
		ya.notify({
			title = "Filee",
			content = "No file selected or hovered",
			level = "warn",
			timeout = 3,
		})
		return
	end

	local idx = ya.which({
		cands = {
			{ on = "w", desc = "WebP (Image)" },
			{ on = "p", desc = "PDF (Document)" },
			{ on = "j", desc = "JPEG (Image)" },
			{ on = "n", desc = "PNG (Image)" },
			{ on = "a", desc = "AVIF (Modern Image)" },
			{ on = "o", desc = "OCR / Text (Markdown)" },
			{ on = "t", desc = "OCR / Plain Text (.txt)" },
			{ on = "m", desc = "MP3 (Extract Audio)" },
			{ on = "v", desc = "MP4 (Video)" },
			{ on = "b", desc = "Remove Background (PNG)" },
			{ on = "z", desc = "ZIP Archive" },
			{ on = "x", desc = "Extract Archive" },
			{ on = "c", desc = "Custom Format..." },
			{ on = "d", desc = "Open in Filee Donut (GUI)" },
		},
	})

	if not idx then
		return
	end

	local format = nil
	local preset = nil

	if idx == 1 then
		format = "webp"
	elseif idx == 2 then
		format = "pdf"
	elseif idx == 3 then
		format = "jpg"
	elseif idx == 4 then
		format = "png"
	elseif idx == 5 then
		format = "avif"
	elseif idx == 6 then
		format = "md"
	elseif idx == 7 then
		format = "txt"
	elseif idx == 8 then
		format = "mp3"
	elseif idx == 9 then
		format = "mp4"
	elseif idx == 10 then
		preset = "remove-background"
	elseif idx == 11 then
		format = "zip"
	elseif idx == 12 then
		preset = "extract"
	elseif idx == 13 then
		local val, event = ya.input({
			title = "Target format or preset (e.g. svg, docx, csv):",
			position = { "center", w = 50 },
		})
		if event ~= 1 or not val or val == "" then
			return
		end
		format = val:gsub("^%.", ""):gsub("%s+", "")
	elseif idx == 14 then
		-- Open in Filee GUI Donut
		local home = os.getenv("HOME") or ""
		local xdg_data = os.getenv("XDG_DATA_HOME")
		local app_bin = (xdg_data and #xdg_data > 0 and xdg_data or (home .. "/.local/share")) .. "/filee/application/Filee"

		local cmd = Command(app_bin)
		cmd:arg("--convert")
		for _, target in ipairs(targets) do
			cmd:arg(target)
		end
		local child, err = cmd:spawn()
		if not child then
			ya.notify({
				title = "Filee Error",
				content = "Could not launch Filee Donut (is Filee installed in " .. app_bin .. "?)",
				level = "error",
				timeout = 5,
			})
			return
		end
		ya.notify({
			title = "Filee",
			content = ("Opened %d file(s) in Filee Donut"):format(#targets),
			timeout = 3,
		})
		return
	end

	local label = (format or preset):upper()
	ya.notify({
		title = "Filee",
		content = ("Converting %d file(s) to %s..."):format(#targets, label),
		timeout = 3,
	})

	local cmd = Command("filee")
	cmd:arg("convert")
	for _, target in ipairs(targets) do
		cmd:arg(target)
	end

	if preset then
		cmd:arg("--preset"):arg(preset)
	elseif format then
		cmd:arg("--to"):arg(format)
	end

	local output, err = cmd:output()

	if output and is_successful(output.status) then
		local msg = ("Converted %d file(s) to %s"):format(#targets, label)
		notify("Filee", msg, "info")
	else
		local err_msg = "Conversion failed"
		if output and output.stderr and #output.stderr > 0 then
			err_msg = output.stderr:gsub("[\r\n]+", " ")
		elseif err then
			err_msg = tostring(err)
		end
		notify("Filee Error", err_msg:sub(1, 120), "error")
	end
end

return M
