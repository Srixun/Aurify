-- Forever 16001 APIs: Ketho/wow-ui-source-forever, branch forever,
-- InstanceDocumentation.lua, SystemDocumentation.lua, MapDocumentation.lua,
-- ClubDocumentation.lua and ChatInfoDocumentation.lua. Guild locations use
-- bounded in-memory guild-only reports, never a website feed or SavedVariables IPC.
local addonName = ...
local frame = CreateFrame("Frame")
local MAX_SESSIONS = 128
local MAX_CHARACTERS = 50
local MIN_TIME, MAX_TIME = 946684800000, 4102444800000
local ready, inWorld, firstWorld = false, false, true
local reloadVisit, leftAt = false, nil
local current, pending
local generation, nextLogCall = 0, 0
local callingLogger = false
local barberOpen = false
local closedAppearance
local gm = { records = {}, revision = 0, enabled = true, inWorld = false }

local function integer(value, minimum, maximum)
    return type(value) == "number" and value == math.floor(value)
        and value >= minimum and value <= maximum
end

local function text(value, maximum)
    return type(value) == "string" and #value > 0 and #value <= maximum
        and not string.find(value, "[%c]")
        and not string.find(value, "^%s") and not string.find(value, "%s$")
end

-- Club IDs are wire identities, not display names. Keep them as canonical
-- decimal text so Lua's number precision cannot alter a future 64-bit value.
local function guildClubKey(value)
    if integer(value, 1, 9007199254740991) then return string.format("%.0f", value) end
    if text(value, 20) and string.match(value, "^[1-9]%d*$") then return value end
end

local function playerGuid(value)
    return text(value, 96) and (string.match(value, "^Player%-%d+%-%x+$")
        or (#value == 18 and string.match(value, "^0x0000%x+$")
            and value ~= "0x0000000000000000"))
end

local function projectSession(value)
    if type(value) ~= "table" or not text(value.id, 96)
        or not string.match(value.id, "^[%w_-]+$")
        or (value.kind ~= "party" and value.kind ~= "raid")
        or not integer(value.instanceId, 0, 2147483647)
        or not integer(value.difficultyId, 0, 2147483647)
        or not text(value.name, 160) or not playerGuid(value.playerGuid)
        or not text(value.realm, 96)
        or not integer(value.startedAt, MIN_TIME, MAX_TIME)
        or (value.endedAt ~= nil and not integer(value.endedAt, value.startedAt, MAX_TIME)) then
        return nil
    end
    return {
        id = value.id, kind = value.kind, instanceId = value.instanceId,
        difficultyId = value.difficultyId, name = value.name,
        startedAt = value.startedAt, endedAt = value.endedAt,
        playerGuid = value.playerGuid, realm = value.realm,
    }
end

local function characterGuid(value)
    if not text(value, 96) then return false end
    local lower = string.lower(value)
    return (string.match(lower, "^player%-%d+%-%x+$")
        and not string.match(lower, "^player%-%d+%-0+$"))
        or (#lower == 18 and string.match(lower, "^0x0000%x+$")
            and lower ~= "0x0000000000000000")
end

local function itemStringMatches(value, itemId)
    if not text(value, 2048) then return false end
    local id, suffix = string.match(value, "^item:(%d+)(.*)$")
    return id and tonumber(id) == itemId
        and (suffix == "" or (string.sub(suffix, 1, 1) == ":"
            and not string.find(suffix, "[^%d:%-]")))
end

local function validIconBuild(value)
    if not text(value, 15) then return false end
    local patch, build = string.match(value, "^1%.60%.(%d+)%.([1-9]%d+)$")
    return patch and #patch <= 3 and (patch == "0" or string.sub(patch, 1, 1) ~= "0")
        and #build >= 4 and #build <= 6
end
local function projectIcon(value)
    if type(value) ~= "table" or not validIconBuild(value.build)
        or not integer(value.fileDataId, 1, 2147483647) then return end
    for _, field in ipairs({ "left", "right", "top", "bottom" }) do
        local coordinate = value[field]
        if type(coordinate) ~= "number" or coordinate ~= coordinate
            or coordinate < 0 or coordinate > 1 then return end
    end
    if value.left >= value.right or value.top >= value.bottom then return end
    return { build = value.build, fileDataId = value.fileDataId,
        left = value.left, right = value.right, top = value.top, bottom = value.bottom }
end

local function plainTooltipText(value)
    return type(value) == "string" and (value == "" or text(value, 512))
        and not string.find(value, "[|<>]") and not string.find(value, "://", 1, true)
        and not string.find(string.lower(value), "www.", 1, true)
end

local function projectTooltip(value)
    if type(value) ~= "table" or type(value.lines) ~= "table"
        or #value.lines < 1 or #value.lines > 64 then return end
    local lines, total = {}, 0
    for _, line in ipairs(value.lines) do
        if type(line) ~= "table" or not plainTooltipText(line.leftText)
            or (line.rightText ~= nil and not plainTooltipText(line.rightText))
            or (line.leftText == "" and (line.rightText == nil or line.rightText == "")) then return end
        total = total + #line.leftText + #(line.rightText or "")
        if total > 8192 then return end
        local projected = { leftText = line.leftText, rightText = line.rightText }
        for _, field in ipairs({ "leftColor", "rightColor" }) do
            if line[field] ~= nil then
                if type(line[field]) ~= "string" or #line[field] ~= 6
                    or string.find(line[field], "[^0-9a-f]") then return end
                projected[field] = line[field]
            end
        end
        lines[#lines + 1] = projected
    end
    return { lines = lines }
end

local function projectAppearance(value, character)
    if type(value) ~= "table" or not validIconBuild(value.build)
        or not integer(value.capturedAt, MIN_TIME, MAX_TIME)
        or value.capturedAt > character.capturedAt + 300000
        or not integer(value.raceId, 1, 2147483647) or value.raceId ~= character.raceId
        or not integer(value.bodyType, 0, 1) or value.bodyType ~= character.bodyType
        or (value.modelId ~= nil and not integer(value.modelId, 1, 2147483647))
        or type(value.choices) ~= "table" or #value.choices < 1 or #value.choices > 128 then return end
    local choices, seen = {}, {}
    for _, choice in ipairs(value.choices) do
        if type(choice) ~= "table" or not integer(choice.optionId, 1, 2147483647)
            or not integer(choice.choiceId, 1, 2147483647) or seen[choice.optionId] then return end
        seen[choice.optionId] = true
        choices[#choices + 1] = { optionId = choice.optionId, choiceId = choice.choiceId }
    end
    table.sort(choices, function(a, b) return a.optionId < b.optionId end)
    return { build = value.build, capturedAt = value.capturedAt, raceId = value.raceId,
        bodyType = value.bodyType, modelId = value.modelId, choices = choices }
end


local function projectEquipment(value)
    if type(value) ~= "table" or not integer(value.slot, 1, 19)
        or not integer(value.itemId, 0, 2147483647) then return end
    if value.itemId == 0 then
        if value.itemString ~= nil or value.name ~= nil
            or value.itemLevel ~= nil or value.quality ~= nil then return end
    elseif not itemStringMatches(value.itemString, value.itemId) then
        return
    end
    if (value.name ~= nil and not text(value.name, 240))
        or (value.itemLevel ~= nil and (type(value.itemLevel) ~= "number"
            or value.itemLevel ~= value.itemLevel
            or value.itemLevel < 0 or value.itemLevel > 100000))
        or (value.quality ~= nil and not integer(value.quality, 0, 7)) then return end
    local row = {
        slot = value.slot, itemId = value.itemId, itemString = value.itemString,
        name = value.name, itemLevel = value.itemLevel, quality = value.quality,
    }
    if value.itemId > 0 and integer(value.iconFileDataId, 1, 2147483647)
        and validIconBuild(value.iconBuild) then
        row.iconFileDataId, row.iconBuild = value.iconFileDataId, value.iconBuild
    end
    if value.itemId > 0 and value.tooltip ~= nil then row.tooltip = projectTooltip(value.tooltip) end
    return row
end

local function projectCharacter(value, at)
    if type(value) ~= "table" or not characterGuid(value.guid)
        or not text(value.name, 80) or not text(value.realm, 96)
        or not integer(value.capturedAt, MIN_TIME, MAX_TIME)
        or (at and value.capturedAt > at + 300000)
        or (value.class ~= nil and not text(value.class, 60))
        or (value.level ~= nil and not integer(value.level, 1, 1000))
        or (value.spec ~= nil and value.spec ~= "" and not text(value.spec, 80))
        or (value.guildRank ~= nil and value.guildRank ~= "" and not text(value.guildRank, 80))
        or (value.guildClubId ~= nil and value.guildClubId ~= "" and not guildClubKey(value.guildClubId))
        or (value.realmMode ~= nil and value.realmMode ~= "pve" and value.realmMode ~= "pvp")
        or type(value.equipment) ~= "table" then return end
    local equipment, seen, count = {}, {}, 0
    for key, row in pairs(value.equipment) do
        count = count + 1
        if count > 19 or not integer(key, 1, 19) then return end
        local projected = projectEquipment(row)
        if not projected or seen[projected.slot] then return end
        seen[projected.slot] = true
        equipment[#equipment + 1] = projected
    end
    for index = 1, count do
        if value.equipment[index] == nil then return end
    end
    table.sort(equipment, function(a, b) return a.slot < b.slot end)
    local result = {
        guid = value.guid, name = value.name, realm = value.realm,
        class = value.class, level = value.level, capturedAt = value.capturedAt,
        spec = value.spec, guildRank = value.guildRank, realmMode = value.realmMode,
        equipment = equipment,
    }
    local guildClubId = guildClubKey(value.guildClubId)
    if guildClubId then result.guildClubId = guildClubId end
    if integer(value.raceId, 1, 2147483647) then result.raceId = value.raceId end
    if text(value.race, 80) then result.race = value.race end
    if integer(value.classId, 1, 2147483647) then result.classId = value.classId end
    if integer(value.bodyType, 0, 1) then result.bodyType = value.bodyType end
    result.classIcon, result.raceIcon = projectIcon(value.classIcon), projectIcon(value.raceIcon)
    result.appearance = projectAppearance(value.appearance, result)
    return result
end

local function retainCharacter(characters, candidate)
    local guid, realm = string.lower(candidate.guid), candidate.realm
    for index, previous in ipairs(characters) do
        if string.lower(previous.guid) == guid and previous.realm == realm then
            if candidate.capturedAt >= previous.capturedAt then characters[index] = candidate end
            return
        end
    end
    characters[#characters + 1] = candidate
    if #characters > MAX_CHARACTERS then
        local oldest = 1
        for index = 2, #characters do
            if characters[index].capturedAt < characters[oldest].capturedAt then oldest = index end
        end
        table.remove(characters, oldest)
    end
end

local function initialize()
    local previous = type(AurifyDB) == "table" and AurifyDB
    local sessions, seen = {}, {}
    if previous and previous.version == 1 and type(previous.sessions) == "table" then
        local count = #previous.sessions
        for index = math.max(1, count - MAX_SESSIONS + 1), count do
            local session = projectSession(previous.sessions[index])
            if session and not seen[session.id] then
                sessions[#sessions + 1] = session
                seen[session.id] = true
            end
        end
    end
    local characters = {}
    if previous and previous.version == 1 and previous.characterSchemaVersion == 1
        and type(previous.characters) == "table" then
        local at = type(time) == "function" and time() * 1000
        if not integer(at, MIN_TIME, MAX_TIME) then at = nil end
        for key, value in pairs(previous.characters) do
            if integer(key, 1, 2147483647) then
                local character = projectCharacter(value, at)
                if character then retainCharacter(characters, character) end
            end
        end
    end
    AurifyDB = {
        version = 1, sessions = sessions,
        characterSchemaVersion = 1, characters = characters,
        guildMapEnabled = not (previous and previous.guildMapEnabled == false),
    }
    ready = true
end

local function now()
    local value = time() * 1000
    if integer(value, MIN_TIME, MAX_TIME) then return value end
end

-- Forever's Camelot PaperDollFrame uses C_PaperDollInfo slot-name lookup and
-- ItemLocation:CreateFromEquipmentSlot. API inventory indices are not our
-- canonical roster slot numbers, so resolve each named slot explicitly.
local equipmentSlots = {
    "HeadSlot", "NeckSlot", "ShoulderSlot", "ShirtSlot", "ChestSlot",
    "WaistSlot", "LegsSlot", "FeetSlot", "WristSlot", "HandsSlot",
    "Finger0Slot", "Finger1Slot", "Trinket0Slot", "Trinket1Slot",
    "BackSlot", "MainHandSlot", "SecondaryHandSlot", "RangedSlot", "TabardSlot",
}

local function query(fn, ...)
    if type(fn) ~= "function" then return false end
    return pcall(fn, ...)
end

local function currentIconBuild()
    local ok, version, build = query(GetBuildInfo)
    if not ok or not text(version, 8) or not text(build, 6) then return end
    local value = version .. "." .. build
    if validIconBuild(value) then return value end
end

-- Forever TextureUtilsDocumentation.lua: use returned file/UVs, never atlas defaults.
local function captureAtlas(atlas, build)
    if not build or not text(atlas, 160) or type(C_Texture) ~= "table" then return end
    local ok, info = query(C_Texture.GetAtlasInfo, atlas)
    if not ok or type(info) ~= "table" then return end
    return projectIcon({ build = build, fileDataId = info.file,
        left = info.leftTexCoord, right = info.rightTexCoord,
        top = info.topTexCoord, bottom = info.bottomTexCoord })
end

-- PlayerInfoSharedDocumentation uses Enum.UnitSex, unlike the numeric UnitSex API.
-- Map named enum members to renderer body types, never subtract guessed ordinals.
local function bodyType(sex)
    if type(Enum) ~= "table" or type(Enum.UnitSex) ~= "table" or sex == nil then return end
    if sex == Enum.UnitSex.Male then return 0 end
    if sex == Enum.UnitSex.Female then return 1 end
end

local function captureIdentity(build, observedClass, observedRace, observedRaceFile)
    local row = {}
    local raceOK, race, raceFile, raceId = query(UnitRace, "player")
    local unitRaceFile = raceOK and text(raceFile, 80) and raceFile or nil
    local guidRaceFile = text(observedRaceFile, 80) and observedRaceFile or nil
    raceFile = unitRaceFile or guidRaceFile
    if raceOK and integer(raceId, 1, 2147483647) then row.raceId = raceId end
    row.race = raceOK and text(race, 80) and race
        or (text(observedRace, 80) and observedRace or raceFile)
    local classOK, _, classFile, classId = query(UnitClass, "player")
    if classOK and integer(classId, 1, 2147483647) then row.classId = classId end
    classFile = classOK and text(classFile, 60) and classFile
        or (text(observedClass, 60) and observedClass or nil)
    if classFile then
        local atlasOK, atlas = query(GetClassAtlas, string.lower(classFile))
        if atlasOK then row.classIcon = captureAtlas(atlas, build) end
    end
    local playerData
    if type(C_PlayerInfo) == "table" then
        local ok, data = query(C_PlayerInfo.GetPlayerCharacterData)
        if ok and type(data) == "table" and text(data.fileName, 80)
            and (not unitRaceFile or data.fileName == unitRaceFile)
            and (not guidRaceFile or data.fileName == guidRaceFile) then
            row.bodyType = bodyType(data.sex)
            row.raceIcon = captureAtlas(data.createScreenIconAtlas, build)
            if not row.raceIcon and row.bodyType ~= nil then
                local atlasOK, atlas = query(GetRaceAtlas, string.lower(data.fileName),
                    row.bodyType == 0 and "male" or "female", true)
                if atlasOK then row.raceIcon = captureAtlas(atlas, build) end
            end
            -- Icon evidence does not relax the existing appearance identity gate.
            if unitRaceFile and data.fileName == unitRaceFile and row.raceId then playerData = data end
        end
    end
    return row, playerData
end

local function stripTooltipText(value)
    if value == nil then return "" end
    if type(value) ~= "string" or #value > 4096 then return end
    -- Never project player/account/crafter links, URLs, or angle-bracket identities.
    for kind in string.gmatch(value, "|H([^:|]+)") do
        if kind ~= "item" and kind ~= "spell" and kind ~= "enchant" then return end
    end
    value = string.gsub(value, "|H[^|]*|h(.-)|h", "%1")
    value = string.gsub(value, "|c%x%x%x%x%x%x%x%x", "")
    value = string.gsub(value, "|r", "")
    value = string.gsub(value, "|T.-|t", "")
    value = string.gsub(value, "|A.-|a", "")
    value = string.gsub(value, "[\r\n\t]", " ")
    value = string.gsub(value, "^%s*(.-)%s*$", "%1")
    if plainTooltipText(value) then return value end
end

local function tooltipColor(color)
    if type(color) ~= "table" then return end
    local values = { color.r, color.g, color.b }
    for index = 1, 3 do
        local value = values[index]
        if type(value) ~= "number" or value ~= value or value < 0 or value > 1 then return end
        values[index] = math.floor(value * 255 + 0.5)
    end
    return string.format("%02x%02x%02x", values[1], values[2], values[3])
end

-- TooltipInfoSharedDocumentation: only item display lines, not unit/owner/seller
-- records, nested blocks, trade recipients or arbitrary tooltip extension fields.
local tooltipLineNames = { "None", "GemSocket", "ItemEnchantmentPermanent", "ItemBinding",
    "EquipSlot", "ItemName", "GemSocketEnchantment", "ItemLevel", "ItemUpgradeLevel",
    "ItemQuality", "UsageRequirement", "ItemSpellTriggerOnUse", "ItemSpellTriggerOnEquip",
    "ItemSpellTriggerOnProc", "SpellDescription", "FlavorText" }
local function tooltipLineIsPublic(line)
    if line.args == nil then return true end
    if type(line.args) ~= "table" then return false end
    for _, argument in pairs(line.args) do
        if type(argument) ~= "table" or argument.guidVal ~= nil
            or argument.stringVal ~= nil then return false end
        if type(argument.field) == "string" then
            local field = string.lower(argument.field)
            if string.find(field, "creator", 1, true) or string.find(field, "crafter", 1, true)
                or string.find(field, "owner", 1, true) or string.find(field, "seller", 1, true)
                or string.find(field, "account", 1, true) or string.find(field, "player", 1, true)
                or string.find(field, "unit", 1, true) then return false end
        end
    end
    return true
end

local function captureTooltip(inventorySlot, itemId)
    if type(C_TooltipInfo) ~= "table" or type(Enum) ~= "table"
        or type(Enum.TooltipDataType) ~= "table" or type(Enum.TooltipDataLineType) ~= "table" then return end
    local ok, data = query(C_TooltipInfo.GetInventoryItem, "player", inventorySlot)
    if not ok or type(data) ~= "table" or data.type ~= Enum.TooltipDataType.Item
        or data.id ~= itemId or type(data.lines) ~= "table" or #data.lines > 128 then return end
    local allowed = {}
    for _, name in ipairs(tooltipLineNames) do
        local id = Enum.TooltipDataLineType[name]
        if id ~= nil then allowed[id] = true end
    end
    local lines, total = {}, 0
    for _, line in ipairs(data.lines) do
        if type(line) == "table" and allowed[line.type] and tooltipLineIsPublic(line) then
            local left, right = stripTooltipText(line.leftText), stripTooltipText(line.rightText)
            if left and right and (left ~= "" or right ~= "") then
                total = total + #left + #right
                if #lines >= 64 or total > 8192 then return end
                lines[#lines + 1] = { leftText = left, rightText = right ~= "" and right or nil,
                    leftColor = tooltipColor(line.leftColor), rightColor = tooltipColor(line.rightColor) }
            end
        end
    end
    if #lines > 0 then return { lines = lines } end
end

-- Read-only naturally opened barber state, or the authoritative applied event.
-- A nil model omits its ID for exact catalog resolution. Never fill pending choices.
local function captureAppearance(character, playerData, build, appearanceApplied)
    if (not barberOpen and not appearanceApplied) or not build or not playerData
        or character.bodyType == nil or type(C_BarberShop) ~= "table" then return end
    local unchangedOK, changed = query(C_BarberShop.HasAnyChanges)
    local dataOK, data = query(C_BarberShop.GetCurrentCharacterData)
    local modelOK, modelId = query(C_BarberShop.GetViewingChrModel)
    local alteredOK, altered = query(C_BarberShop.IsViewingAlteredForm)
    if not unchangedOK or changed ~= false or not dataOK or type(data) ~= "table"
        or data.fileName ~= playerData.fileName or data.sex ~= playerData.sex
        or not modelOK or (modelId ~= nil and not integer(modelId, 1, 2147483647))
        or not alteredOK or altered ~= false then return end
    local categoriesOK, categories = query(C_BarberShop.GetAvailableCustomizations)
    if not categoriesOK or type(categories) ~= "table" or #categories < 1 or #categories > 128 then return end
    local choices, seen = {}, {}
    for _, category in ipairs(categories) do
        if type(category) ~= "table" or type(category.options) ~= "table"
            or #category.options > 128 or category.spellShapeshiftFormID ~= nil
            or (category.chrModelID ~= nil and category.chrModelID ~= modelId) then return end
        for _, option in ipairs(category.options) do
            if type(option) ~= "table" or not integer(option.id, 1, 2147483647)
                or seen[option.id] or type(option.choices) ~= "table"
                or not integer(option.currentChoiceIndex, 1, #option.choices) then return end
            local choice = option.choices[option.currentChoiceIndex]
            if type(choice) ~= "table" or not integer(choice.id, 1, 2147483647)
                or choice.ineligibleChoice ~= false or choice.isLocked ~= false then return end
            seen[option.id] = true
            choices[#choices + 1] = { optionId = option.id, choiceId = choice.id }
            if #choices > 128 then return end
        end
    end
    local stillOK, stillChanged = query(C_BarberShop.HasAnyChanges)
    local sameModelOK, sameModel = query(C_BarberShop.GetViewingChrModel)
    local sameDataOK, sameData = query(C_BarberShop.GetCurrentCharacterData)
    if not stillOK or stillChanged ~= false or not sameModelOK or sameModel ~= modelId
        or not sameDataOK or type(sameData) ~= "table"
        or sameData.fileName ~= data.fileName or sameData.sex ~= data.sex then return end
    return projectAppearance({ build = build, capturedAt = character.capturedAt,
        raceId = character.raceId, bodyType = character.bodyType,
        modelId = modelId, choices = choices }, character)
end

local function captureEquipment(slot, slotName, build)
    if type(C_PaperDollInfo) ~= "table" or type(ItemLocation) ~= "table"
        or type(C_Item) ~= "table" then return end
    local slotOK, inventorySlot = query(C_PaperDollInfo.GetInventorySlotInfo, slotName)
    if not slotOK or not integer(inventorySlot, 1, 2147483647) then return end
    local locationOK, location = query(ItemLocation.CreateFromEquipmentSlot, ItemLocation, inventorySlot)
    if not locationOK or type(location) ~= "table" then return end
    local existsOK, exists = query(C_Item.DoesItemExist, location)
    if not existsOK or type(exists) ~= "boolean" then return end
    -- The documented existence predicate is distinct from item-cache state.
    -- A failed query, nil link, or pending metadata never proves an empty slot.
    if not exists then return { slot = slot, itemId = 0 } end
    local linkOK, link = query(C_Item.GetItemLink, location)
    if not linkOK or type(link) ~= "string" then return end
    local itemString = string.match(link, "|H(item:[^|]+)|h") or link
    local itemId = tonumber(string.match(itemString, "^item:(%d+)"))
    if not integer(itemId, 1, 2147483647) or not itemStringMatches(itemString, itemId) then return end
    local row = { slot = slot, itemId = itemId, itemString = itemString }
    local infoOK, name, _, quality, itemLevel = query(C_Item.GetItemInfo, link)
    if infoOK then
        if text(name, 240) then row.name = name end
        if integer(quality, 0, 7) then row.quality = quality end
        if type(itemLevel) == "number" and itemLevel >= 0 and itemLevel <= 100000 then
            row.itemLevel = itemLevel
        end
    end
    if build then
        local iconOK, icon = query(C_Item.GetItemIcon, location)
        if iconOK and integer(icon, 1, 2147483647) then
            row.iconFileDataId, row.iconBuild = icon, build
        end
    end
    -- Confirm variant stability across cache/tooltip calls before accepting metadata.
    local tooltipOK, tooltip = pcall(captureTooltip, inventorySlot, itemId)
    local sameOK, sameLink = query(C_Item.GetItemLink, location)
    if not sameOK or sameLink ~= link then return end
    if tooltipOK then row.tooltip = tooltip end
    return row
end

-- Forever SpecializationInfoDocumentation.lua: nullable results are unavailable,
-- not evidence that the player has no specialization.
local function captureSpecialization()
    if type(C_SpecializationInfo) ~= "table" then return end
    local readyOK, initialized = query(C_SpecializationInfo.IsInitialized)
    if not readyOK or initialized ~= true then return end
    local indexOK, index = query(C_SpecializationInfo.GetSpecialization)
    if not indexOK or not integer(index, 1, 2147483647) then return end
    local infoOK, specId, name = query(C_SpecializationInfo.GetSpecializationInfo, index, false, false)
    if infoOK and integer(specId, 1, 2147483647) and text(name, 80) then return name end
end

-- Club APIs return nothing until initialized. Only a definite false membership
-- result proves absence; a missing club/member/rank must not clear a known rank.
local function captureGuild(guid)
    local memberOK, guilded = query(IsPlayerInGuildFromGUID, guid)
    if not memberOK then return end
    if guilded == false then return "", "" end
    if guilded ~= true or type(C_Club) ~= "table" then return end
    local clubOK, clubId = query(C_Club.GetGuildClubId)
    local guildClubId = clubOK and guildClubKey(clubId) or nil
    if not guildClubId then return end
    local infoOK, info = query(C_Club.GetMemberInfoForSelf, clubId)
    local guildRank = infoOK and type(info) == "table"
        and text(info.guildRank, 80) and info.guildRank or nil
    return guildClubId, guildRank
end

local function captureCharacter(appearanceApplied, closed)
    if not ready then return end
    local guidOK, guid = query(UnitGUID, "player")
    if not guidOK or not characterGuid(guid) then return end
    -- Forever PlayerScript documents class/race/name/realm/level at 2/3-4/6/7/8.
    -- Only the logged-in player's identity and own guild membership are queried.
    local infoOK, _, class, race, raceFile, _, name, realm, level = query(GetPlayerInfoByGUID, guid)
    if not infoOK or not text(name, 80) then return end
    if realm == nil or realm == "" then
        local realmOK, playerRealm = query(GetRealmName)
        if realmOK then realm = playerRealm end
    end
    if not text(realm, 96) then return end
    local equipment = {}
    local build = currentIconBuild()
    local identityOK, identity, playerData = pcall(captureIdentity, build, class, race, raceFile)
    if not identityOK then identity, playerData = {}, nil end
    for slot, slotName in ipairs(equipmentSlots) do
        local row = captureEquipment(slot, slotName, build)
        if row then equipment[#equipment + 1] = row end
    end
    local spec = captureSpecialization()
    local guildClubId, guildRank = captureGuild(guid)
    local sameOK, sameGuid = query(UnitGUID, "player")
    local at = now()
    if not sameOK or sameGuid ~= guid or not at then return end
    local candidate = {
        guid = guid, name = name, realm = realm, capturedAt = at,
        class = text(class, 60) and class or nil,
        level = integer(level, 1, 1000) and level or nil,
        spec = spec, guildRank = guildRank, guildClubId = guildClubId,
        equipment = equipment,
        raceId = identity.raceId, race = identity.race, bodyType = identity.bodyType,
        classId = identity.classId, raceIcon = identity.raceIcon, classIcon = identity.classIcon,
    }
    local appearanceOK, appearance = pcall(captureAppearance, candidate, playerData, build, appearanceApplied)
    local freshAppearance = appearanceOK and appearance
    if freshAppearance then candidate.appearance = freshAppearance end
    if not candidate.appearance and appearanceApplied and closed
        and string.lower(closed.guid) == string.lower(guid) and closed.realm == realm
        and closed.appearance and closed.appearance.build == build then
        candidate.appearance = projectAppearance(closed.appearance, candidate)
    end
    if not candidate.appearance and build and not appearanceApplied then
        for _, previous in ipairs(AurifyDB.characters) do
            if string.lower(previous.guid) == string.lower(guid) and previous.realm == realm
                and previous.appearance and previous.appearance.build == build then
                candidate.appearance = projectAppearance(previous.appearance, candidate)
                break
            end
        end
    end
    local finalOK, finalGuid = query(UnitGUID, "player")
    if finalOK and finalGuid == guid then
        retainCharacter(AurifyDB.characters, candidate)
        if freshAppearance then return freshAppearance, guid, realm end
    end
end

local captureScheduled = false
local function cancelCharacterCapture()
    captureScheduled = false
    frame:SetScript("OnUpdate", nil)
end

local function scheduleCharacterCapture()
    if not ready or not inWorld or captureScheduled then return end
    captureScheduled = true
    local elapsedTotal = 0
    frame:SetScript("OnUpdate", function(_, elapsed)
        closedAppearance = nil
        elapsedTotal = elapsedTotal + elapsed
        if elapsedTotal < 0.25 then return end
        cancelCharacterCapture()
        if inWorld then captureCharacter() end
    end)
end

local function sameInstance(session, kind, instanceId, difficultyId, guid, realm)
    return session and session.endedAt == nil and session.kind == kind
        and session.instanceId == instanceId and session.difficultyId == difficultyId
        and session.playerGuid == guid and session.realm == realm
end

local function cancelStart()
    generation = generation + 1
    pending = nil
end

-- /combatlog calls the global LoggingCombat (Forever SlashCommands.lua).
-- Secure hooks see ordinary external setters, including explicit nil setters.
-- They cannot prove that other addons did not cache a pre-hook function reference.
-- Therefore Aurify NEVER calls LoggingCombat(false), even after its own enable:
-- existing/manual logging cannot be accidentally stopped on exit or UI reload.
if type(hooksecurefunc) == "function" and type(LoggingCombat) == "function" then
    pcall(hooksecurefunc, "LoggingCombat", function(...)
        if callingLogger then return end
        nextLogCall = GetTime() + 11
        if select("#", ...) > 0 then cancelStart() end
    end)
end

local tryStart
local function scheduleStart(delay, token)
    if not C_Timer or type(C_Timer.After) ~= "function" then
        pending = nil
        return
    end
    if pending.scheduled then return end
    pending.scheduled = true
    C_Timer.After(delay, function()
        if pending and generation == token then
            pending.scheduled = false
            tryStart(token)
        end
    end)
end

tryStart = function(token)
    if not pending or pending.scheduled or token ~= generation or not inWorld then return end
    local inside, kind = IsInInstance()
    if not inside or (kind ~= "party" and kind ~= "raid") then
        cancelStart()
        return
    end
    local wait = nextLogCall - GetTime()
    if wait > 0 then
        scheduleStart(wait, token)
        return
    end
    -- At most three attempts, >=11s apart; each uses <=2 of the shared 5/10s
    -- allowance. A nil return is UNKNOWN, not permission to change the logger.
    pending.attempts = pending.attempts + 1
    nextLogCall = GetTime() + 11
    callingLogger = true
    local ok, state = pcall(LoggingCombat)
    if ok and state == false then ok, state = pcall(LoggingCombat, true) end
    callingLogger = false
    if not ok or state == true or pending.attempts >= 3 then
        pending = nil
    else
        scheduleStart(11, token)
    end
end

local function beginLogging()
    cancelStart()
    if type(LoggingCombat) ~= "function" then return end
    pending = { attempts = 0 }
    tryStart(generation)
end

local function finish(at)
    cancelStart()
    if current and at and at >= current.startedAt then current.endedAt = at end
    current = nil
end

local function newId(at, instanceId)
    local prefix = string.format("%.0f-%d-", at, instanceId)
    local suffix = 1
    while true do
        local id, found = prefix .. suffix, false
        for _, session in ipairs(AurifyDB.sessions) do
            if session.id == id then found = true; break end
        end
        if not found then return id end
        suffix = suffix + 1
    end
end

local function reconcile()
    if not ready or not inWorld then return end
    local at = now()
    if not at then return end
    local inside, kind = IsInInstance()
    if not inside or (kind ~= "party" and kind ~= "raid") then
        if current then finish(leftAt or at) end
        leftAt, reloadVisit = nil, false
        return
    end
    local name, instanceType, difficultyId, _, _, _, _, instanceId = GetInstanceInfo()
    local guid, realm = UnitGUID("player"), GetRealmName()
    -- Do not invent zero IDs or identities while the client is transitioning.
    if instanceType ~= kind or not integer(instanceId, 0, 2147483647)
        or not integer(difficultyId, 0, 2147483647) or not text(name, 160)
        or not playerGuid(guid) or not text(realm, 96) then return end
    if sameInstance(current, kind, instanceId, difficultyId, guid, realm) then
        leftAt = nil
        return
    end
    if current then finish(leftAt or at) end
    leftAt = nil
    if reloadVisit then
        -- Only the most recent visit by this character can survive a UI reload.
        -- Initial login never resumes a stale open visit after logout/crash.
        for index = #AurifyDB.sessions, 1, -1 do
            local session = AurifyDB.sessions[index]
            if session.playerGuid == guid and session.realm == realm then
                if sameInstance(session, kind, instanceId, difficultyId, guid, realm)
                    and session.startedAt <= at then current = session end
                break
            end
        end
    end
    reloadVisit = false
    if not current then
        current = {
            id = newId(at, instanceId), kind = kind, instanceId = instanceId,
            difficultyId = difficultyId, name = name, startedAt = at,
            playerGuid = guid, realm = realm,
        }
        local sessions = AurifyDB.sessions
        if #sessions >= MAX_SESSIONS then table.remove(sessions, 1) end
        sessions[#sessions + 1] = current
    end
    beginLogging()
end

frame:SetScript("OnEvent", function(_, event, ...)
    if event ~= "BARBER_SHOP_APPEARANCE_APPLIED" and event ~= "BARBER_SHOP_CLOSE" then
        closedAppearance = nil
    end
    if event == "ADDON_LOADED" then
        if ... == addonName then initialize() end
    elseif event == "PLAYER_ENTERING_WORLD" then
        if not ready then initialize() end
        local _, reloading = ...
        inWorld = true
        if firstWorld then reloadVisit = reloading == true end
        reconcile()
        scheduleCharacterCapture()
        firstWorld = false
        if pending then tryStart(generation) end
    elseif event == "PLAYER_LEAVING_WORLD" then
        -- Preserve the observed departure until the destination is known.
        -- This event also fires during logout/reload, so it cannot end a visit.
        if current then leftAt = now() end
        cancelCharacterCapture()
        captureCharacter()
        inWorld = false
    elseif event == "PLAYER_LOGOUT" then
        -- Also fires for /reload. Never fabricate an end/restart at this event.
        -- Client persistence writes AurifyDB now; it is not a live IPC channel.
        cancelCharacterCapture()
        captureCharacter() -- Still query the player after PLAYER_LEAVING_WORLD.
        inWorld = false
    elseif event == "BARBER_SHOP_OPEN" then
        barberOpen = true
        scheduleCharacterCapture()
    elseif event == "BARBER_SHOP_CLOSE" then
        -- Blizzard_BarbershopUI/Mainline/Blizzard_BarberShopUI.lua: APPLIED calls
        -- Cancel(), which can emit synchronous CLOSE before our APPLIED handler.
        -- Order/getter availability is not guaranteed. Keep only a fresh API read.
        closedAppearance = nil
        if barberOpen then
            local appearance, guid, realm = captureCharacter()
            if appearance then closedAppearance = { appearance = appearance, guid = guid, realm = realm } end
        end
        barberOpen = false
        scheduleCharacterCapture()
    elseif event == "BARBER_SHOP_APPEARANCE_APPLIED" then
        local closed = closedAppearance
        closedAppearance = nil
        -- Invalidate the old vector first; never promote a draft or retained cache.
        local ok, guid = query(UnitGUID, "player")
        if (not ok or not characterGuid(guid)) and closed then ok, guid = true, closed.guid end
        if ok and characterGuid(guid) then
            for _, character in ipairs(AurifyDB.characters) do
                if string.lower(character.guid) == string.lower(guid) then character.appearance = nil end
            end
        end
        captureCharacter(true, closed)
        scheduleCharacterCapture()
    elseif event == "BARBER_SHOP_RESULT" then
        if ... == true then scheduleCharacterCapture() end
    elseif event == "BARBER_SHOP_FORCE_CUSTOMIZATIONS_UPDATE"
        or event == "BARBER_SHOP_COST_UPDATE" or event == "BARBER_SHOP_CAMERA_VALUES_UPDATED"
        or event == "TOOLTIP_DATA_UPDATE" then
        scheduleCharacterCapture()
    elseif event == "PLAYER_EQUIPMENT_CHANGED"
        or event == "PLAYER_TALENT_UPDATE" or event == "ACTIVE_TALENT_GROUP_CHANGED"
        or event == "SPEC_INVOLUNTARILY_CHANGED"
        or event == "PLAYER_LEVEL_CHANGED" or event == "PLAYER_LEVEL_UP"
        or event == "GUILD_ROSTER_UPDATE" or event == "GUILD_RANKS_UPDATE"
        or event == "GUILD_RANKS_UPDATE_ACTIVE_PLAYER" or event == "PLAYER_GUILD_UPDATE"
        or event == "CLUB_MEMBER_UPDATED" or event == "CLUB_MEMBERS_UPDATED"
        or event == "CLUB_SELF_MEMBER_ROLE_UPDATED" then
        scheduleCharacterCapture()
    elseif event == "UNIT_LEVEL" then
        if ... == "player" then scheduleCharacterCapture() end
    elseif event == "GET_ITEM_INFO_RECEIVED" or event == "ITEM_DATA_LOAD_RESULT" then
        local _, success = ...
        if success == true then scheduleCharacterCapture() end
    else
        reconcile()
    end
end)

-- Unknown events can throw on Forever; only evidenced events, guarded anyway.
for _, event in ipairs({ "ADDON_LOADED", "PLAYER_ENTERING_WORLD",
    "PLAYER_LEAVING_WORLD", "PLAYER_LOGOUT", "ZONE_CHANGED_NEW_AREA",
    "GROUP_ROSTER_UPDATE", "PLAYER_EQUIPMENT_CHANGED",
    "GET_ITEM_INFO_RECEIVED", "ITEM_DATA_LOAD_RESULT",
    "PLAYER_TALENT_UPDATE", "ACTIVE_TALENT_GROUP_CHANGED", "SPEC_INVOLUNTARILY_CHANGED",
    "PLAYER_LEVEL_CHANGED", "PLAYER_LEVEL_UP", "UNIT_LEVEL",
    "GUILD_ROSTER_UPDATE", "GUILD_RANKS_UPDATE", "GUILD_RANKS_UPDATE_ACTIVE_PLAYER",
    "PLAYER_GUILD_UPDATE", "CLUB_MEMBER_UPDATED", "CLUB_MEMBERS_UPDATED",
    "CLUB_SELF_MEMBER_ROLE_UPDATED", "TOOLTIP_DATA_UPDATE",
    "BARBER_SHOP_OPEN", "BARBER_SHOP_CLOSE", "BARBER_SHOP_APPEARANCE_APPLIED",
    "BARBER_SHOP_RESULT", "BARBER_SHOP_FORCE_CUSTOMIZATIONS_UPDATE",
    "BARBER_SHOP_COST_UPDATE", "BARBER_SHOP_CAMERA_VALUES_UPDATED" }) do
    pcall(frame.RegisterEvent, frame, event)
end


-- Guild locations are ephemeral and are never included in uploaded snapshots.
do
    local PREFIX, MAX_MEMBERS, MAX_SESSIONS = "AurifyGuildMap", 2000, 16
    local MAX_INT, MILLION = 2147483647, 1000000
    gm.ttl = 20
    local control = CreateFrame("Frame")
    local members, names, replay, classes = {}, {}, {}, {}
    local group = {}
    local guildId, guildWire, ownGuid, ownRealm, realmName, ownClass
    local rosterReady, dirty, registered, loaded = false, true, false, false
    local session, sequence, nextSample, nextRoster = nil, 0, 0, 0
    local lastAttempt, lastMap, lastX, lastY, lastScope = -8, nil, nil, nil, nil
    local lastSend = "not sent"

    -- Protect only engine calls and engine-owned field access, not feature logic.
    local function call(fn, ...)
        if type(fn) ~= "function" then return false end
        return pcall(fn, ...)
    end

    local function public(value)
        if type(issecretvalue) == "function" and issecretvalue(value) then return false end
        if type(canaccessvalue) == "function" and not canaccessvalue(value) then return false end
        return true
    end

    local function field(value, key)
        if not public(value) or type(value) ~= "table" then return nil end
        local ok, result = pcall(function() return value[key] end)
        if ok and public(result) then return result end
    end

    local function text(value, limit)
        return public(value) and type(value) == "string" and #value > 0
            and #value <= limit and not value:find("[%c|]")
            and not value:find("^%s") and not value:find("%s$")
    end

    local function integer(value, low, high)
        return public(value) and type(value) == "number" and value >= low
            and value <= high and value == math.floor(value)
    end

    local function clock()
        local ok, value = call(GetTime)
        if ok and public(value) and type(value) == "number"
            and value >= 0 and value < math.huge then return value end
    end

    local function clubKey(value)
        if integer(value, 1, 9007199254740991) then return string.format("%.0f", value) end
        if text(value, 20) and value:match("^[1-9]%d*$") then return value end
    end

    local function clear()
        if next(gm.records) then
            for guid in pairs(gm.records) do gm.records[guid] = nil end
            gm.revision = gm.revision + 1
        end
    end

    local function remove(guid)
        if gm.records[guid] then
            gm.records[guid] = nil
            gm.revision = gm.revision + 1
        end
    end

    local function online(presence)
        return integer(presence, 0, 5) and (presence == 1 or presence == 4 or presence == 5)
    end

    local function nameKey(name)
        if not text(name, 160) then return nil end
        local base, realm = name:match("^([^%-]+)%-(.+)$")
        if not base then base, realm = name, ownRealm end
        if not text(base, 80) or not text(realm, 96) then return nil end
        -- Only the engine's own realm aliases can collapse. Never strip a foreign realm.
        if realmName and realm == realmName then realm = ownRealm end
        if not realm then return nil end
        return base .. "-" .. realm
    end

    local function classFor(classID)
        if not integer(classID, 1, MAX_INT) then return nil end
        if classes[classID] then return classes[classID] end
        local ok, info = call(C_CreatureInfo and C_CreatureInfo.GetClassInfo, classID)
        local token = ok and field(info, "classFile")
        if text(token, 32) and token:match("^[A-Z]+$") then
            classes[classID], classes[token] = token, true
            return token
        end
    end

    local function sameInstanceVisible(guid)
        if guid == ownGuid then return true end
        local unit = group[guid]
        if not unit or gm.currentInInstance ~= true then return false end
        -- Group/map IDs alone do not identify an instance copy. Require a locally
        -- visible group unit and recheck its GUID because unit slots can be reused.
        local guidOK, current = call(UnitGUID, unit)
        if not guidOK or not public(current) or current ~= guid then return false end
        local ok, visible = call(UnitIsVisible, unit)
        return ok and public(visible) and visible == true
    end

    local function updateVisibility(guid, record)
        local grouped, visible = group[guid] ~= nil, sameInstanceVisible(guid)
        if record.grouped ~= grouped or record.sameInstanceVisible ~= visible then
            record.grouped, record.sameInstanceVisible = grouped, visible
            gm.revision = gm.revision + 1
        end
    end

    local function refreshGroup()
        local fresh = {}
        if ownGuid then fresh[ownGuid] = "player" end
        for index = 1, 44 do
            local unit = index <= 4 and ("party" .. index) or ("raid" .. (index - 4))
            local ok, guid = call(UnitGUID, unit)
            if ok and text(guid, 96) then fresh[guid] = unit end
        end
        group = fresh
        for guid, record in pairs(gm.records) do
            updateVisibility(guid, record)
        end
    end

    local function refreshScope()
        local ok, instanced = call(IsInInstance)
        if not ok or not public(instanced) or type(instanced) ~= "boolean" then instanced = nil end
        if gm.currentInInstance ~= instanced then
            gm.currentInInstance = instanced
            gm.revision = gm.revision + 1
        end
        if instanced ~= nil then return instanced and "i" or "w" end
    end

    local function invalidateGuild(id, key)
        clear()
        if guildWire ~= key then
            members, names, replay = {}, {}, {}
            guildId, guildWire = id, key
            lastMap, lastX, lastY = nil, nil, nil
            gm.revision = gm.revision + 1
        end
        rosterReady = false
    end

    local function checkGuild()
        local ok, id = call(C_Club and C_Club.GetGuildClubId)
        local key = ok and clubKey(id) or nil
        if not key then invalidateGuild(nil, nil); return false end
        if key ~= guildWire then invalidateGuild(id, key); dirty = true end
        local readyOK, value = call(C_Club and C_Club.AreMembersReady, id)
        if not readyOK or not public(value) or value ~= true then
            clear()
            rosterReady, dirty = false, true
            return false
        end
        return true
    end

    local function refreshRoster(at)
        dirty, nextRoster = false, at + 8
        if not checkGuild() then return end
        local realmOK, realm = call(GetNormalizedRealmName)
        ownRealm = realmOK and text(realm, 96) and realm or nil
        local rawOK, raw = call(GetRealmName)
        realmName = rawOK and text(raw, 96) and raw or nil
        local guidOK, guid = call(UnitGUID, "player")
        ownGuid = guidOK and text(guid, 96) and guid or nil
        refreshGroup()
        local classOK, _, token, classID = call(UnitClass, "player")
        ownClass = classOK and text(token, 32) and classFor(classID) == token and token or nil
        local ok, ids = call(C_Club and C_Club.GetClubMembers, guildId)
        if not ok or not public(ids) or type(ids) ~= "table" then
            clear(); rosterReady = false; return
        end
        local lengthOK, count = pcall(function() return #ids end)
        if not lengthOK or not integer(count, 0, MAX_MEMBERS) then
            clear(); rosterReady = false; return
        end
        local fresh, bindings = {}, {}
        for index = 1, count do
            local memberId = field(ids, index)
            if memberId ~= nil then
                local infoOK, info = call(C_Club and C_Club.GetMemberInfo, guildId, memberId)
                if infoOK then
                    local name, memberGuid = field(info, "name"), field(info, "guid")
                    local key = nameKey(name)
                    local validGuid = text(memberGuid, 96)
                    if key then
                        if bindings[key] ~= nil then bindings[key] = false
                        else bindings[key] = validGuid and memberGuid or false end
                    end
                    if validGuid and key then
                        if fresh[memberGuid] ~= nil then fresh[memberGuid] = false
                        else
                            local class = field(info, "classID")
                            fresh[memberGuid] = { guid = memberGuid, name = name, key = key,
                                classID = class, classFile = classFor(class),
                                presence = field(info, "presence") }
                        end
                    end
                end
            end
        end
        members, names, rosterReady = fresh, bindings, true
        for memberGuid, record in pairs(gm.records) do
            local member = members[memberGuid]
            if not member or names[member.key] ~= memberGuid or not online(member.presence) then
                remove(memberGuid)
            elseif record.name ~= member.name then
                record.name = member.name
                gm.revision = gm.revision + 1
            end
        end
        for memberGuid in pairs(replay) do
            if not members[memberGuid] then replay[memberGuid] = nil end
        end
        -- Membership changes invalidate renderer caches even without a current location.
        gm.revision = gm.revision + 1
    end

    local function activeMember(guid)
        local member = guid and members[guid]
        if member and names[member.key] == guid and online(member.presence) then return member end
    end

    local function put(member, mapId, x, y, classFile, instanced, at)
        if mapId == 0 then remove(member.guid); return end
        local record = gm.records[member.guid]
        if not record then
            record = { guid = member.guid }
            gm.records[member.guid] = record
        end
        if record.mapId ~= mapId or record.x ~= x or record.y ~= y
            or record.classFile ~= classFile or record.name ~= member.name
            or record.instanced ~= instanced then
            gm.revision = gm.revision + 1
        end
        record.name, record.classFile = member.name, classFile
        record.mapId, record.x, record.y, record.seen = mapId, x, y, at
        record.instanced = instanced
        updateVisibility(member.guid, record)
    end

    local function register()
        if registered then return end
        local ok, result = call(C_ChatInfo and C_ChatInfo.RegisterAddonMessagePrefix, PREFIX)
        -- Forever RegisterAddonMessagePrefixResult: Success=0, DuplicatePrefix=1.
        registered = ok and public(result) and (result == 0 or result == 1 or result == true)
    end

    local function allowed()
        if not checkGuild() then return false end
        if not registered or not rosterReady or dirty or not gm.enabled or not gm.inWorld
            or not activeMember(ownGuid) or not ownClass then return false end
        local ok, restricted = call(C_ChatInfo and C_ChatInfo.AreOutgoingAddonChatMessagesRestricted)
        return ok and public(restricted) and restricted == false
    end

    local function send(mapId, x, y, scope, at)
        if not session or sequence >= MAX_INT or not allowed() then return end
        sequence = sequence + 1
        local message = string.format("1|%s|%s|%d|%d|%d|%d|%s|%s", guildWire,
            session, sequence, mapId, x, y, ownClass, scope)
        if #message >= 200 then return end
        -- Consume both sequence and cadence even if the engine's result is ambiguous.
        -- A later normal sample/heartbeat is a new message, never a retry of this packet.
        lastAttempt, lastMap, lastX, lastY, lastScope = at, mapId, x, y, scope
        local ok, result = call(C_ChatInfo and C_ChatInfo.SendAddonMessage, PREFIX, message, "GUILD")
        if ok and public(result) and (result == 0 or result == true or result == nil) then
            lastSend = "sent"
        else lastSend = "not confirmed" end
    end

    local function withdraw()
        local at = clock()
        if at and lastMap and lastMap ~= 0 then send(0, 0, 0, lastScope, at) end
    end

    function gm.SetEnabled(value)
        if type(value) ~= "boolean" then return end
        if not value then withdraw() end
        if gm.enabled ~= value then gm.revision = gm.revision + 1 end
        gm.enabled = value
        if type(AurifyDB) == "table" then AurifyDB.guildMapEnabled = value end
        clear()
        if value then dirty, nextSample = true, 0 end
    end

    local function receive(prefix, message, channel, sender)
        if not public(prefix) or not public(channel) or prefix ~= PREFIX or channel ~= "GUILD"
            or not gm.enabled or not gm.inWorld or not registered or not rosterReady or dirty
            or not public(message) or type(message) ~= "string" or #message >= 200
            or message:find("[%c]") or not checkGuild() then return end
        local key = nameKey(sender)
        local guid = key and names[key]
        local member = guid and activeMember(guid)
        if not member or guid == ownGuid then return end
        local version, club, token, seqText, mapText, xText, yText, classFile, scope = message:match(
            "^(%d+)|(%d+)|([%w_-]+)|(%d+)|(%d+)|(%d+)|(%d+)|([A-Z]+)|([wi])$")
        if version ~= "1" or club ~= guildWire or not token or #token > 32
            or not classes[classFile] or (member.classFile and member.classFile ~= classFile) then return end
        local seq, mapId, x, y = tonumber(seqText), tonumber(mapText), tonumber(xText), tonumber(yText)
        if not integer(seq, 1, MAX_INT) or not integer(mapId, 0, MAX_INT)
            or not integer(x, 0, MILLION) or not integer(y, 0, MILLION)
            or (mapId == 0 and (x ~= 0 or y ~= 0)) then return end
        local at = clock()
        if not at then return end
        local state = replay[guid]
        if not state then
            state = { session = token, sequence = 0, retired = {}, history = {}, cursor = 0 }
            replay[guid] = state
        elseif state.session ~= token then
            -- Remember the most recent retired sessions without permanently blocking reloads.
            -- Nonces have no remote-clock authority; history is intentionally bounded.
            if state.retired[token] then return end
            state.cursor = state.cursor % MAX_SESSIONS + 1
            local oldest = state.history[state.cursor]
            if oldest then state.retired[oldest] = nil end
            state.history[state.cursor] = state.session
            state.retired[state.session] = true
            state.session, state.sequence = token, 0
        end
        if seq <= state.sequence then return end
        state.sequence = seq
        put(member, mapId, x / MILLION, y / MILLION, classFile, scope == "i", at)
    end

    local function sample(at, scope)
        local member = activeMember(ownGuid)
        if not member or not ownClass then if ownGuid then remove(ownGuid) end; return end
        if not scope then remove(ownGuid); withdraw(); return end
        local ok, mapId = call(C_Map and C_Map.GetBestMapForUnit, "player")
        local x, y
        if ok and integer(mapId, 1, MAX_INT) then
            local positionOK, position = call(C_Map and C_Map.GetPlayerMapPosition, mapId, "player")
            if positionOK and public(position) then
                local getXY = field(position, "GetXY")
                local xyOK, px, py = call(getXY, position)
                if xyOK and public(px) and public(py) and type(px) == "number"
                    and type(py) == "number" and px >= 0 and px <= 1 and py >= 0 and py <= 1 then
                    x, y = math.floor(px * MILLION + 0.5), math.floor(py * MILLION + 0.5)
                end
            end
        end
        if not x then mapId, x, y = 0, 0, 0 end
        put(member, mapId, x / MILLION, y / MILLION, ownClass, scope == "i", at)
        local moving = mapId ~= lastMap or x ~= lastX or y ~= lastY or scope ~= lastScope
        if at - lastAttempt >= 8 or (moving and at - lastAttempt >= 1) then
            send(mapId, x, y, scope, at)
        end
    end

    local function initializeCore()
        if loaded then return end
        loaded = true
        gm.enabled = not (type(AurifyDB) == "table" and AurifyDB.guildMapEnabled == false)
        register()
        local at = clock() or 0
        local ok, seconds = call(time)
        if not ok or not integer(seconds, 0, 9007199254740991) then seconds = 0 end
        -- A per-load nonce only; receivers never compare clocks or order these tokens.
        session = string.format("%x%x%06x%06x", seconds % 4294967296,
            math.floor(at * 1000) % 4294967296, math.random(0, 16777215), math.random(0, 16777215))
    end

    local function feedback(message)
        if DEFAULT_CHAT_FRAME and type(DEFAULT_CHAT_FRAME.AddMessage) == "function" then
            DEFAULT_CHAT_FRAME:AddMessage("Aurify: " .. message)
        end
    end

    -- One private command key, no Blizzard or other addon handler replacement.
    if type(SlashCmdList) == "table" and SlashCmdList.AURIFYGUILDMAP == nil then
        local collision = false
        for key, value in pairs(_G) do
            if type(key) == "string" and key:match("^SLASH_")
                and public(value) and type(value) == "string" and value:lower() == "/aurify" then
                collision = true; break
            end
        end
        if not collision then
            SLASH_AURIFYGUILDMAP1 = "/aurify"
            SlashCmdList.AURIFYGUILDMAP = function(input)
                if not public(input) or type(input) ~= "string" then return end
                local command = input:lower():match("^%s*(.-)%s*$")
                if command == "map on" then gm.SetEnabled(true); feedback("guild map on.")
                elseif command == "map off" then gm.SetEnabled(false); feedback("guild map off.")
                elseif command == "map status" then
                    local count, at = 0, clock()
                    for _, record in pairs(gm.records) do
                        if at and at - record.seen < gm.ttl then count = count + 1 end
                    end
                    feedback("guild map " .. (gm.enabled and "on" or "off")
                        .. "; " .. (gm.inWorld and "in world" or "out of world")
                        .. "; " .. (rosterReady and not dirty and "guild ready" or "guild unavailable")
                        .. "; prefix " .. (registered and "registered" or "unavailable")
                        .. "; " .. count .. " fresh locations; last report " .. lastSend .. ".")
                else feedback("/aurify map on | off | status") end
            end
        end
    end

    control:SetScript("OnEvent", function(_, event, ...)
        if event == "CHAT_MSG_ADDON" then receive(...)
        elseif event == "ADDON_LOADED" then
            if ... == addonName then initializeCore() end
        elseif event == "PLAYER_ENTERING_WORLD" then
            initializeCore()
            gm.inWorld, dirty, nextSample = true, true, 0
            gm.revision = gm.revision + 1
            register()
            refreshScope()
            refreshGroup()
        elseif event == "PLAYER_LEAVING_WORLD" or event == "PLAYER_LOGOUT" then
            withdraw()
            gm.inWorld, gm.currentInInstance = false, nil
            clear()
            gm.revision = gm.revision + 1
        elseif event == "GROUP_ROSTER_UPDATE" then refreshGroup()
        elseif event == "CLUB_MEMBER_UPDATED" or event == "CLUB_MEMBERS_UPDATED"
            or event == "CLUB_SELF_MEMBER_ROLE_UPDATED" then
            if guildWire and clubKey(...) == guildWire then dirty = true end
        else dirty = true end
    end)

    control:SetScript("OnUpdate", function()
        if not loaded or not gm.enabled or not gm.inWorld then return end
        local at = clock()
        if not at or at < nextSample then return end
        nextSample = at + 0.25
        local scope = refreshScope()
        if not checkGuild() then return end
        if dirty or at >= nextRoster then refreshRoster(at) end
        if not rosterReady then return end
        for guid, record in pairs(gm.records) do
            if at - record.seen >= gm.ttl then remove(guid)
            elseif record.instanced then updateVisibility(guid, record) end
        end
        sample(at, scope)
    end)

    for _, event in ipairs({ "ADDON_LOADED", "PLAYER_ENTERING_WORLD", "PLAYER_LEAVING_WORLD",
        "PLAYER_LOGOUT", "CHAT_MSG_ADDON", "PLAYER_GUILD_UPDATE", "GUILD_ROSTER_UPDATE",
        "CLUB_MEMBER_UPDATED", "CLUB_MEMBERS_UPDATED", "CLUB_SELF_MEMBER_ROLE_UPDATED",
        "GROUP_ROSTER_UPDATE" }) do
        call(control.RegisterEvent, control, event)
    end
end

-- Native MapCanvas overlays; the collector and Blizzard/Overlord pins stay untouched.
do
    local providers, mapInfoCache = {}, {}
    local mapInfoCount = 0
    local REFRESH_INTERVAL = 0.25

    local function finite(value)
        return type(value) == "number" and value == value
            and value > -math.huge and value < math.huge
    end

    local function normalized(x, y)
        return finite(x) and finite(y) and x >= 0 and x <= 1 and y >= 0 and y <= 1
    end

    local function mapInfo(mapId)
        local info = mapInfoCache[mapId]
        if info then return info end
        local ok
        ok, info = query(C_Map and C_Map.GetMapInfo, mapId)
        if not ok or type(info) ~= "table" then return end
        if mapInfoCount >= 256 then mapInfoCache, mapInfoCount = {}, 0 end
        mapInfoCache[mapId], mapInfoCount = info, mapInfoCount + 1
        return info
    end

    local function hasFlag(info, flag)
        return type(info.flags) == "number" and math.floor(info.flags / flag) % 2 == 1
    end

    -- A rectangle is meaningful only after verifying the actual parent chain.
    -- Limit traversal to tolerate malformed/cyclic engine data without guessing.
    local function isAncestor(source, target)
        local current = source
        for _ = 1, 64 do
            if current == target then return true end
            local info = mapInfo(current)
            if not info or type(info.parentMapID) ~= "number" then return end
            if info.parentMapID <= 0 then return false end
            if info.parentMapID == current then return false end
            -- UIMapFlag.IgnoreInTranslationsToParent (Forever MapConstants).
            if hasFlag(info, 2097152) then return false end
            current = info.parentMapID
        end
        return false
    end

    local function relation(provider, source, target)
        local cached = provider.relations[source]
        if cached ~= nil then return cached end
        local sourceInfo, targetInfo = mapInfo(source), mapInfo(target)
        if not sourceInfo or not targetInfo then return end
        local ancestor = isAncestor(source, target)
        if ancestor == nil then return end
        if ancestor then
            local ok, minX, maxX, minY, maxY = query(C_Map.GetMapRectOnMap, source, target)
            if ok and finite(minX) and finite(maxX) and finite(minY) and finite(maxY)
                and maxX > minX and maxY > minY then
                cached = { minX, maxX - minX, minY, maxY - minY }
            end
        end
        if not cached then
            -- NoWorldPositions / DoNotTranslateBranches: never force an engine
            -- world transform through a map that explicitly disallows it.
            if hasFlag(sourceInfo, 128) or hasFlag(targetInfo, 128)
                or hasFlag(sourceInfo, 512) or hasFlag(targetInfo, 512) then
                cached = false
            else
                cached = "world"
            end
        end
        provider.relations[source] = cached
        return cached
    end

    local function project(provider, record, target)
        if not normalized(record.x, record.y) then return end
        if record.mapId == target then return record.x, record.y end
        local transform = relation(provider, record.mapId, target)
        if type(transform) == "table" then
            local x = transform[1] + transform[2] * record.x
            local y = transform[3] + transform[4] * record.y
            if normalized(x, y) then return x, y end
            return
        end
        if transform ~= "world" or not provider.vector then return end
        provider.vector:SetXY(record.x, record.y)
        local ok, continent, world = query(C_Map.GetWorldPosFromMapPos, record.mapId, provider.vector)
        if not ok or not finite(continent) or not world then return end
        local worldOK, worldX, worldY = query(world.GetXY, world)
        if not worldOK or not finite(worldX) or not finite(worldY) then return end
        local positionOK, mapId, position = query(C_Map.GetMapPosFromWorldPos, continent, world, target)
        if not positionOK or mapId ~= target or not position then return end
        local xyOK, x, y = query(position.GetXY, position)
        if not xyOK or not normalized(x, y) then return end
        -- Confirm the selected map really shares this world/instance space.
        -- An override returning another map or a different world is not a fixup.
        local reverseOK, reverseContinent, reverseWorld = query(C_Map.GetWorldPosFromMapPos, target, position)
        if not reverseOK or reverseContinent ~= continent or not reverseWorld then return end
        local reverseXY, reverseX, reverseY = query(reverseWorld.GetXY, reverseWorld)
        if reverseXY and finite(reverseX) and finite(reverseY)
            and math.abs(reverseX - worldX) <= 0.1 and math.abs(reverseY - worldY) <= 0.1 then
            return x, y
        end
    end

    local function classColor(classFile)
        local ok, color = query(C_ClassColor and C_ClassColor.GetClassColor, classFile)
        if ok and color then
            local rgbOK, r, g, b = query(color.GetRGB, color)
            if rgbOK and finite(r) and finite(g) and finite(b) then return r, g, b end
        end
        local rgbOK, r, g, b = query(GetClassColor, classFile)
        if rgbOK and finite(r) and finite(g) and finite(b) then return r, g, b end
        color = RAID_CLASS_COLORS and RAID_CLASS_COLORS[classFile]
        if color then return color.r, color.g, color.b end
        return 1, 1, 1
    end

    local function acquire(provider)
        local pin = table.remove(provider.free)
        if pin then return pin end
        pin = CreateFrame("Frame", nil, provider.root)
        pin:SetSize(1, 1)
        pin:EnableMouse(false)
        -- Only glyphs counter-scale. Scaling the coordinate anchor would move it.
        pin.glyph = CreateFrame("Frame", nil, pin)
        pin.glyph:SetSize(10, 10)
        pin.glyph:SetPoint("CENTER", pin, "CENTER")
        pin.glyph:EnableMouse(false)
        pin.dot = pin.glyph:CreateTexture(nil, "OVERLAY")
        pin.dot:SetAllPoints()
        pin.dot:SetTexture("Interface\\COMMON\\Indicator-Gray")
        pin.label = pin.glyph:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
        pin.label:SetFont(STANDARD_TEXT_FONT, 12, "OUTLINE")
        pin.label:SetPoint("LEFT", pin.glyph, "RIGHT", 3, 0)
        pin.label:SetJustifyH("LEFT")
        pin:Hide()
        return pin
    end

    local function release(provider, guid, pin)
        pin:Hide()
        pin.x, pin.y, pin.sourceMap, pin.sourceX, pin.sourceY = nil, nil, nil, nil, nil
        pin.name, pin.classFile = nil, nil
        provider.active[guid] = nil
        provider.free[#provider.free + 1] = pin
    end

    local function visibleRecord(record, now)
        return record and now >= record.seen and now - record.seen < gm.ttl
            and (not record.instanced or (gm.currentInInstance == true
                and record.grouped == true and record.sameInstanceVisible == true))
    end

    local Provider = {}

    function Provider:OnAdded(map)
        MapCanvasDataProviderMixin.OnAdded(self, map)
        if not self.root then
            self.root = CreateFrame("Frame", nil, map:GetCanvas())
            self.root:SetAllPoints(map:GetCanvas())
            self.root:EnableMouse(false)
            self.active, self.free, self.relations = {}, {}, {}
            self.vector = CreateVector2D and CreateVector2D(0, 0)
        end
        self.visible = map:IsShown()
        self:RefreshAllData()
    end

    function Provider:RemoveAllData()
        if not self.root then return end
        for guid, pin in pairs(self.active) do release(self, guid, pin) end
        self.root:Hide()
        self.revision = nil
    end

    function Provider:OnRemoved(map)
        self:RemoveAllData()
        self.visible = false
        MapCanvasDataProviderMixin.OnRemoved(self, map)
    end

    function Provider:OnShow()
        self.visible = true
        self:RefreshAllData()
    end

    function Provider:OnHide()
        self.visible = false
        self:RemoveAllData()
    end

    function Provider:OnMapChanged()
        self.mapId = nil
        self:RefreshAllData()
    end

    function Provider:OnCanvasSizeChanged()
        self.width = nil
        self:RefreshAllData()
    end

    function Provider:OnCanvasScaleChanged()
        self.scale = nil
        self:RefreshAllData()
    end

    -- Pan is inherited from the canvas; never run projection for a pure pan.
    function Provider:OnCanvasPanChanged() end

    function Provider:RefreshAllData()
        local map = self:GetMap()
        if not map or not self.visible or not map:IsVisible() or not gm.enabled or not gm.inWorld then
            self:RemoveAllData()
            return
        end
        local mapId = map:GetMapID()
        local displayOK, hideIcons = query(C_Map and C_Map.GetMapDisplayInfo, mapId)
        if not finite(mapId) or not displayOK or hideIcons ~= false then
            self:RemoveAllData()
            return
        end
        local canvas = map:GetCanvas()
        local width, height = canvas:GetWidth(), canvas:GetHeight()
        local canvasScale = canvas:GetEffectiveScale()
        if not finite(width) or not finite(height) or width <= 0 or height <= 0
            or not finite(canvasScale) or canvasScale <= 0 then
            self:RemoveAllData()
            return
        end
        local scale = UIParent:GetEffectiveScale() / canvasScale
        local mapChanged = self.mapId ~= mapId
        local layoutChanged = mapChanged or self.width ~= width or self.height ~= height
        local scaleChanged = self.scale ~= scale
        local dataChanged = self.revision ~= gm.revision
        if mapChanged then self.relations = {} end
        self.mapId, self.width, self.height, self.scale = mapId, width, height, scale
        local now = GetTime()
        for guid, pin in pairs(self.active) do
            local record = gm.records[guid]
            if not visibleRecord(record, now) then
                release(self, guid, pin)
            end
        end
        for guid, record in pairs(gm.records) do
            if visibleRecord(record, now) then
                local pin = self.active[guid]
                if not pin then
                    pin = acquire(self)
                    self.active[guid] = pin
                end
                local moved = mapChanged or pin.sourceMap ~= record.mapId
                    or pin.sourceX ~= record.x or pin.sourceY ~= record.y
                if moved or pin.x == nil then
                    pin.x, pin.y = project(self, record, mapId)
                    pin.sourceMap, pin.sourceX, pin.sourceY = record.mapId, record.x, record.y
                end
                if pin.x then
                    if moved or layoutChanged or not pin:IsShown() then
                        pin:ClearAllPoints()
                        pin:SetPoint("CENTER", self.root, "TOPLEFT", width * pin.x, -height * pin.y)
                    end
                    if scaleChanged or not pin:IsShown() then pin.glyph:SetScale(scale) end
                    if dataChanged or pin.name ~= record.name or pin.classFile ~= record.classFile
                        or not pin:IsShown() then
                        if pin.name ~= record.name then
                            pin.label:SetText((string.gsub(record.name, "|", "||")))
                            pin.name = record.name
                        end
                        if pin.classFile ~= record.classFile or not pin:IsShown() then
                            local r, g, b = classColor(record.classFile)
                            pin.dot:SetVertexColor(r, g, b)
                            pin.label:SetTextColor(r, g, b)
                            pin.classFile = record.classFile
                        end
                    end
                    pin:Show()
                else
                    pin:Hide()
                end
            end
        end
        self.revision = gm.revision
        self.root:Show()
    end

    local function attach(map)
        if not map or type(map.AddDataProvider) ~= "function" or type(map.GetCanvas) ~= "function"
            or not map:GetCanvas() or type(MapCanvasDataProviderMixin) ~= "table"
            or type(CreateFromMixins) ~= "function" then return end
        local provider = providers[map]
        if provider and provider:GetMap() then return end
        if not provider then
            provider = CreateFromMixins(MapCanvasDataProviderMixin, Provider)
            providers[map] = provider
        end
        map:AddDataProvider(provider)
    end

    local lifecycle = CreateFrame("Frame")
    lifecycle:SetScript("OnEvent", function(_, event)
        if event == "PLAYER_LEAVING_WORLD" or event == "PLAYER_LOGOUT" then
            for _, provider in pairs(providers) do provider:RemoveAllData() end
            return
        end
        attach(WorldMapFrame)
        attach(BattlefieldMapFrame)
    end)
    local elapsed = 0
    lifecycle:SetScript("OnUpdate", function(_, delta)
        elapsed = elapsed + delta
        if elapsed < REFRESH_INTERVAL then return end
        elapsed = 0
        for _, provider in pairs(providers) do
            if provider.visible then provider:RefreshAllData() end
        end
    end)
    for _, event in ipairs({ "ADDON_LOADED", "PLAYER_ENTERING_WORLD", "PLAYER_LEAVING_WORLD", "PLAYER_LOGOUT" }) do
        pcall(lifecycle.RegisterEvent, lifecycle, event)
    end
    attach(WorldMapFrame)
    attach(BattlefieldMapFrame)
end
