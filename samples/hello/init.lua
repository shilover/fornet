-- 示例扩展包（docs/EXTENSIONS_API.md）。跑在沙箱里：只有 API 和标准库副本，没有 require / io / os.*。
return function(API)
    -- 1) 登记一个同意键：文案由扩展包给，弹窗 / 记录 / 隐私说明页都由基底管
    API.consent.register{ id = "share", title = "分享问候", titleEn = "Share a greeting",
        zh = { "开启后，这个示例扩展会把一句问候放进联机状态包发给对手。" },
        en = { "When enabled, this sample puts a greeting into the online status packet sent to the opponent." } }

    local greet = nil
    -- 2) 状态包扩展段：x.hello.g=<值>（对方没装就忽略）
    API.status.addField("g", function() return greet end)
    -- 3) 对方的回话：基底把 x.hello.* 分发到这里
    API.lobby.onReply(function(kv) if kv.g then API.log.out("对方问候：%s", kv.g) end end)
    -- 4) 面板：系统设置页加一行；自己的页
    API.panel.addRows("settings", function(items, T)
        items[#items + 1] = { label = T("示例扩展：", "SAMPLE: ") .. (greet or T("未开启", "off")),
                              act = function() API.consent.ask("share", function() greet = greet and nil or "hi"; API.log.out("greet=%s", tostring(greet)) end) end }
    end)
    API.panel.addPage{ id = "about", title = "关于示例扩展", titleEn = "About the sample",
        build = function(items, T) items[#items + 1] = { label = T("这是 docs/EXTENSIONS_API.md 里的示例。传输：", "Sample from docs/EXTENSIONS_API.md. Transport: ") .. API.net.transport() } end }
    -- 5) 房间设置（X1 只登记）
    API.room.addSetting{ key = "hg", zh = "示例：问候", en = "Sample: greeting", default = 0, consent = "share" }
    return { name = "hello", version = "0.1.0" }
end
