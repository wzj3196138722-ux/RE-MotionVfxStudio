-- Display names and grouping only: original paths, values and IDs stay intact.
local M={}
local names={
    Life="生命周期",PtLife="粒子生命周期",Spawn="生成",SpawnExpression="生成表达式",RGBCommon="通用颜色",RgbCommon="通用颜色",
    TypeBillboard2D="二维公告板",TypeBillboard3D="三维公告板",TypeRibbonFollow="跟随拖尾",TypeRibbonLength="定长拖尾",
    TypeRibbonChain="链式拖尾",TypeRibbonFixEnd="固定末端拖尾",TypeRibbonParticle="粒子拖尾",TypeMesh="网格渲染",
    TypePolygon="多边形渲染",TypeNoDraw="不绘制",Transform2D="二维变换",Transform3D="三维变换",Velocity2D="二维速度",Velocity3D="三维速度",
    UVSequence="UV 序列帧",UVScroll="UV 滚动",RGB="RGB",RGBA="RGBA",Uvar="用户变量",GPUP="GPU 粒子",
    Lerp="线性插值",from="起始值",to="结束值",lo="下限",hi="上限",s="普通值",r="随机值",
    Sin="正弦",Cos="余弦",Asin="反正弦",Acos="反余弦",Floor="向下取整",Ceil="向上取整",Log="对数",Exp="指数",
    Abs="绝对值",Saturate="限定到 0～1",Clamp="范围限制",SmoothStep="平滑插值",Pow="幂",Remap="范围映射",
    AppearFrame="出现帧数",KeepFrame="保持帧数",KeepHoldFrame="保持停留帧数",VanishFrame="消失帧数",
    SizeScalar="整体尺寸",UniqueID="唯一标识",IsTypeAttribute="是否渲染类型属性",FixRandomGenerator="固定随机生成器",
    spawnNum="生成数量",spawnNumRange="生成数量随机值",spawnRate="生成速率",spawnRateRange="生成速率随机值",
    unkn="未知字段",unknown="未知字段",unk="未知字段",Pt="粒子",Gpu="GPU",Type="类型",Common="通用",
    Color="颜色",Alpha="透明度",Brightness="亮度",Intensity="强度",Luminance="亮度",Emissive="自发光",Emission="发射",
    Position="位置",Translation="位移",Translate="移动",Rotation="旋转",Rotate="旋转",Scale="缩放",Size="尺寸",
    Velocity="速度",Speed="速度",Acceleration="加速度",Angular="角",Direction="方向",Normal="法线",Tangent="切线",
    Local="局部",World="世界",Parent="父级",Root="根节点",Offset="偏移",Pivot="轴心",Center="中心",Origin="原点",
    Random="随机",Rand="随机",Range="范围",Scalar="整体",Min="最小",Max="最大",Minimum="最小",Maximum="最大",
    Start="起始",End="结束",Begin="开始",Finish="结束",Initial="初始",Final="最终",Base="基础",Default="默认",
    Frame="帧",Time="时间",Timer="计时器",LifeTime="寿命",Duration="持续时间",Delay="延迟",Interval="间隔",
    Hold="停留",Keep="保持",Vanish="消失",Appear="出现",Fade="淡化",In="进入",Out="退出",Rate="速率",Ratio="比例",
    Count="数量",Num="数量",Number="编号",Index="索引",ID="ID",Id="ID",Name="名称",Hash="哈希",Path="路径",
    Texture="贴图",Tex="贴图",Uv="UV",UV="UV",Sequence="序列帧",Scroll="滚动",Filter="过滤",Unit="单元",
    Mesh="网格",Material="材质",Mdf="MDF",Pfb="PFB",Prefab="预制体",Efx="EFX",Resource="资源",File="文件",
    Billboard="公告板",Ribbon="拖尾",Trail="轨迹",Follow="跟随",Length="长度",Width="宽度",Height="高度",Depth="深度",
    Particle="粒子",Particles="粒子",Emitter="发射器",Emit="发射",Spawn="生成",Shape="形状",Layout="布局",
    Flags="标志位",Flag="标志位",Bits="位标记",Bit="位",Mask="遮罩",Enabled="启用",Enable="启用",Disable="禁用",
    Use="使用",Is="是否",Has="含有",Mode="模式",Order="顺序",Sort="排序",Priority="优先级",Settings="设置",
    Expression="表达式",Expressions="表达式",Clip="曲线片段",Curve="曲线",Channel="通道",Key="关键帧",Keys="关键帧",
    Value="数值",Values="数值",Parameter="参数",Parameters="参数",Component="分量",Components="分量",Data="数据",
    Assignment="赋值",Assign="赋值",Operator="运算",Operation="操作",Modifier="修正",Coef="系数",Coefficient="系数",
    Add="附加",Multiply="乘数",Mul="乘数",Power="指数",Const="常量",Constant="常量",Amplitude="振幅",Frequency="频率",
    Repeat="重复",Loop="循环",Cycle="周期",Phase="相位",Seed="种子",Generator="生成器",Table="表",Version="版本",
    Blend="混合",BlendMode="混合模式",Additive="叠加",Opacity="不透明度",Distortion="扭曲",Refraction="折射",
    Roughness="粗糙度",Metallic="金属度",Specular="高光",Diffuse="漫反射",Reflect="反射",Reflection="反射",
    Lighting="光照",Light="光源",Shadow="阴影",Ambient="环境",Fog="雾",Bloom="泛光",Blur="模糊",Soft="柔化",
    Noise="噪声",Flow="流动",Map="图",Dissolve="溶解",Threshold="阈值",Contrast="对比度",Saturation="饱和度",
    Hue="色相",Red="红",Green="绿",Blue="蓝",Ch="通道",Correction="校正",High="高",Low="低",Pass="通道",
    Collision="碰撞",Collider="碰撞体",Gravity="重力",Wind="风",Drag="阻力",Force="力",Attractor="吸引器",
    Influence="影响",Vector="向量",Field="场",Fluid="流体",Physics="物理",Heat="热量",Source="源",Target="目标",
    Culling="剔除",Occlusion="遮挡",Distance="距离",Angle="角度",Axis="轴",Radius="半径",Diameter="直径",
    Bone="骨骼",Bones="骨骼",Joint="关节",Attachment="挂点",Bind="绑定",Binding="绑定",Relation="关系",Relations="关系",
    Animation="动画",Anim="动画",Motion="动作",Action="动作",Actions="动作",Entry="特效块",Entries="特效块",
    Group="分组",Groups="分组",Effect="特效",Effects="特效",Behavior="行为",Trigger="触发器",Event="事件",
    Limit="限制",Buffer="缓冲",Compute="计算",Shader="着色器",Render="渲染",Draw="绘制",Screen="屏幕",Space="空间",
    Stretch="拉伸",Snap="贴合",Terrain="地形",Projection="投影",Orthographic="正交",Volume="体积",Volumetric="体积",
    Water="水体",Float="浮动",Plane="平面",Sphere="球体",Box="盒体",Cone="锥体",Capsule="胶囊体",Line="线",
    Point="点",Cloud="云",Segment="段",Subdivision="细分",Divide="分割",Division="分割",Detail="细节",Resolution="分辨率",
    Simple="简化",Advanced="高级",Custom="自定义",Global="全局",Independent="独立",Ignore="忽略",Options="选项",
    Inherit="继承",Inherited="继承",Reverse="反向",Flip="翻转",Facing="朝向",Camera="相机",View="视图",
    RibbonFollow="跟随拖尾",RibbonLength="定长拖尾",FixEnd="固定末端",Lightweight="轻量",Chain="链式",
    Lightning="闪电",Modular="模块化",Vortexel="涡流体素",Receiver="接收器",Freezer="冻结器",Mixer="混合器",
    Play="播放",Stop="停止",Update="更新",EnableUpdate="启用更新",HighPass="高通",LowPass="低通",
}
local lower={};for key,value in pairs(names) do lower[key:lower()]=value end
-- Display strings are immutable. Bound caches independently of open files and
-- typed values; never cache fields, selection, drafts or search results.
local label_cache={values={},order={},next=1}
local search_cache={values={},order={},next=1}
local function remember(cache,key,value)
    if #key>4096 then return value end
    if #cache.order<4096 then cache.order[#cache.order+1]=key
    else
        cache.values[cache.order[cache.next]]=nil
        cache.order[cache.next]=key;cache.next=cache.next%4096+1
    end
    cache.values[key]=value;return value
end
local bilingual_arguments={["起始值 from"]="from",["结束值 to"]="to",["输入 t"]="t",["输入 value"]="value",
    ["下限 lo"]="lo",["上限 hi"]="hi",["起点 lo"]="lo",["终点 hi"]="hi"}
function M.chinese(raw)
    raw=tostring(raw or "")
    if names[raw] or lower[raw:lower()] then return names[raw] or lower[raw:lower()] end
    -- Translate identifier words, retain array indices, axes and unresearched terms.
    -- A label is never used as the serialized field address.
    local split=raw:gsub("(%u)(%u%l)","%1 %2"):gsub("(%l)(%u)","%1 %2")
    split=split:gsub("[%a]+",function(word) return lower[word:lower()] or word end)
    return split:gsub(" +", " ")
end
function M.label(raw,language)
    raw=tostring(raw or "")
    local key=(language=="en" and "en:" or "zh:")..raw
    if label_cache.values[key]~=nil then return label_cache.values[key] end
    -- Legacy expression metadata supplies bilingual argument names.
    for cn,en in pairs(bilingual_arguments) do raw=raw:gsub(cn,en) end
    if language=="en" then return remember(label_cache,key,raw) end
    local cn=M.chinese(raw)
    return remember(label_cache,key,cn~=raw and (cn.."（"..raw.."）") or raw)
end
function M.search(raw,filter)
    if filter=="" then return true end
    raw=tostring(raw or "")
    local text=search_cache.values[raw]
    if not text then
        local words=raw:gsub("[%w_]+",function(word) return M.chinese(word) end)
        text=remember(search_cache,raw,(raw.." "..M.chinese(raw).." "..words):lower())
    end
    return text:find(filter,1,true)~=nil
end
local function numeric(field)
    return field and type(field.type)=="string" and field.type:match("^System%.") and
        ({Single=true,Double=true,Int16=true,Int32=true,Int64=true,UInt16=true,UInt32=true,UInt64=true,Byte=true,SByte=true,Decimal=true})[field.type:match("[^.]+$")]
end
local function range(field)
    local owner=tostring(field.ownerType or "")
    return owner:match("[.+]Range$") or owner:match("[.+]RangeI$")
end
function M.groups(fields,excluded)
    local by,units,unit_order={}, {}, {}
    for _,f in ipairs(fields) do by[f.path]=f end
    local consumed={}
    local function add(key,rows,axes)
        local unit={path=key,rows=rows,axes=axes,parameter_group=true,fields={},editable=false}
        for _,row in ipairs(rows) do for _,f in ipairs(row.fields) do
            unit.fields[#unit.fields+1]=f;consumed[f.path]=true
            unit.editable=unit.editable or f.editable==true
        end end
        units[key]=unit;unit_order[#unit_order+1]=key;return unit
    end
    -- A real via.Range/RangeI is base s + random r, independent of wire order.
    for _,f in ipairs(fields) do
        local key=f.path:match("^(.*)%.s$")
        if key and range(f) and numeric(f) and not (excluded or {})[f.path] then
            local r=by[key..".r"]
            if numeric(r) and range(r) and not (excluded or {})[r.path] then
                add(key,{{kind="normal",fields={f}},{kind="random",fields={r}}})
            end
        end
    end
    for _,f in ipairs(fields) do
        if not consumed[f.path] and not (excluded or {})[f.path] and numeric(f) then add(f.path,{{kind="normal",fields={f}}}) end
    end
    -- Combine XY or XYZ siblings. An existing Z/W must never be silently
    -- discarded, including a read-only/non-numeric or color-owned component.
    for _,key in ipairs(unit_order) do
        local prefix,suffix=key:match("^(.-)X(.*)$")
        local axis="X"
        if not prefix then prefix,suffix=key:match("^(.*[.])x([^%a]*)$");axis="x" end
        if key=="x" then prefix,suffix,axis="","","x" end
        if prefix then
            local ykey=prefix..(axis=="X" and "Y" or "y")..suffix
            local zkey=prefix..(axis=="X" and "Z" or "z")..suffix
            local wkey=prefix..(axis=="X" and "W" or "w")..suffix
            local x,yy,z=units[key],units[ykey],units[zkey]
            local function exists(path) return by[path] or by[path..".s"] or by[path..".r"] end
            local pair=not z and not exists(zkey)
            if x and yy and (z or pair) and not exists(wkey) and not units[wkey] and #x.rows==#yy.rows and (not z or #x.rows==#z.rows) then
                local valid=true
                for i,row in ipairs(x.rows) do
                    if #row.fields~=1 or #yy.rows[i].fields~=1 or (z and #z.rows[i].fields~=1) or
                        row.fields[1].type~=yy.rows[i].fields[1].type or (z and row.fields[1].type~=z.rows[i].fields[1].type) then valid=false end
                end
                if valid then
                    local rows={};for i,row in ipairs(x.rows) do rows[i]={kind=row.kind,fields={row.fields[1],yy.rows[i].fields[1]}}
                        if z then rows[i].fields[3]=z.rows[i].fields[1] end
                    end
                    units[key],units[ykey],units[zkey]=nil,nil,nil
                    local groupKey=(prefix..suffix):gsub("%.$","")
                    if groupKey=="" then groupKey=z and "Vector3" or "Vector2" end
                    add(groupKey,rows,z and {"X","Y","Z"} or {"X","Y"})
                end
            end
        end
    end
    -- Explicit named random companions (e.g. Rotation + RotationRandom).
    -- Range alone is not guessed to mean randomness; via.Range has typed evidence above.
    local keys={};for key in pairs(units) do keys[#keys+1]=key end;table.sort(keys)
    for _,key in ipairs(keys) do
        local base=key:match("^(.-)Random$") or key:match("^(.-)Rand$") or key:match("^(.-)_random$")
        local random,normal=units[key],base and units[base]
        if random and normal and #random.rows==1 and #normal.rows==1 and #random.fields==#normal.fields then
            local valid=true
            for i,f in ipairs(normal.fields) do if f.type~=random.fields[i].type then valid=false end end
            if valid then
                units[key],units[base]=nil,nil
                add(base,{{kind="normal",fields=normal.fields},{kind="random",fields=random.fields}},normal.axes)
            end
        end
    end
    local members={}
    for _,unit in pairs(units) do
        if #unit.fields>1 then for _,f in ipairs(unit.fields) do members[f.path]=unit end end
    end
    return members
end
return M
