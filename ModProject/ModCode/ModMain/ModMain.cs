using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using HarmonyLib;

namespace MOD_SNs4Ii
{
    /// <summary>
    /// 宗門職位自動整編 MOD。
    /// 玩家不必是宗主；只要玩家目前有宗門，就掃描同宗門 NPC，排除玩家本人，
    /// 依境界/等級與天驕狀態排序，重新分配大長老、長老、真傳、內門、外門。
    /// </summary>
    public class ModMain
    {
        private const string VERSION = "v1";
        private const int CHECK_INTERVAL_FRAMES = 300;

        // 初版先採保守配額；之後可依遊戲實測 log 調整。
        private const int BIG_ELDER_SLOTS = 1;
        private const int ELDER_SLOTS = 3;
        private const int TRUE_DISCIPLE_SLOTS = 10;
        private const int INNER_DISCIPLE_SLOTS = 30;

        // 每個遊戲月份只自動整編一次，避免玩家打開 UI 或切場景時反覆洗職位。
        private static bool worldEnterHooked = false;
        private static bool loopRunning = false;
        private static string lastRunKey = "";
        private static int runCount = 0;

        private static readonly string[] BigElderNames = new[] { "BigElders", "SchoolBigElders", "SchoolBigElder", "BigElder", "bigElder", "WarBigElder" };
        private static readonly string[] ElderNames = new[] { "Elders", "SchoolElders", "SchoolElder", "Elder", "elder", "WarElder" };
        private static readonly string[] TrueDiscipleNames = new[] { "Inherit", "SchoolInherit", "inherit", "Disciple", "SchoolDisciple", "TrueDisciple" };
        private static readonly string[] InnerDiscipleNames = new[] { "In", "SchoolIn", "SchoolInner", "Inner", "inner", "SchoolInnerDisciple", "InnerDisciple" };
        private static readonly string[] OuterDiscipleNames = new[] { "Out", "SchoolOut", "SchoolOuter", "Outer", "outer", "SchoolOuterDisciple", "OuterDisciple" };

        private static MethodInfo cachedIsHeroMethod = null;
        private static bool isHeroMethodSearched = false;

        private static void Log(string msg)
        {
            MelonLogger.Msg("[SchoolRank " + VERSION + "] " + msg);
        }

        public void Init()
        {
            Log("=== Init start ===");
            TryDumpSchoolPostTypeEnum();

            if (!worldEnterHooked)
            {
                try
                {
                    g.events.On(EGameType.IntoWorld, new Action(() =>
                    {
                        Log("[WORLD] IntoWorld fired");
                        ScheduleRankRefresh("IntoWorld", true);
                    }));
                    worldEnterHooked = true;
                    Log("OK IntoWorld hook");
                }
                catch (Exception ex)
                {
                    Log("WARN IntoWorld hook failed: " + ex.Message);
                }
            }

            StartLoop();
            Log("=== Init done ===");
        }

        public void Destroy()
        {
            loopRunning = false;
            Log("Destroy");
        }

        private static void StartLoop()
        {
            if (loopRunning) return;
            loopRunning = true;
            g.timer.Frame(new Action(() =>
            {
                loopRunning = false;
                try
                {
                    ScheduleRankRefresh("Periodic", false);
                }
                catch (Exception ex)
                {
                    Log("[LOOP] failed: " + ex.Message);
                }
                StartLoop();
            }), CHECK_INTERVAL_FRAMES, false);
        }

        private static void ScheduleRankRefresh(string reason, bool forceDelay)
        {
            int delay = forceDelay ? 180 : 1;
            g.timer.Frame(new Action(() =>
            {
                try
                {
                    AutoRankCurrentSchool(reason);
                }
                catch (Exception ex)
                {
                    Log("[RANK] fatal: " + ex);
                }
            }), delay, false);
        }

        private static void AutoRankCurrentSchool(string reason)
        {
            WorldUnitBase player = null;
            try { player = g.world.playerUnit; } catch { }
            if (player == null)
            {
                Log("[RANK] skip: no playerUnit");
                return;
            }

            string playerId = GetUnitId(player);
            dynamic playerData = null;
            try { playerData = player.data.unitData; } catch { }
            if (playerData == null)
            {
                Log("[RANK] skip: no player unitData");
                return;
            }

            string schoolId = SafeString(GetMemberValue(playerData, "schoolID"));
            if (string.IsNullOrEmpty(schoolId) || schoolId == "0" || schoolId == "-1")
            {
                Log("[RANK] skip: player has no school");
                return;
            }

            string runKey = schoolId + ":" + GetGameMonthKey();
            if (lastRunKey == runKey)
            {
                return;
            }
            lastRunKey = runKey;
            runCount++;

            List<MemberInfoEx> members = CollectSchoolMembers(schoolId, playerId);
            if (members.Count == 0)
            {
                Log("[RANK] skip: no NPC members for school=" + schoolId + " reason=" + reason);
                return;
            }

            members.Sort(CompareMember);

            int big = 0, elder = 0, trueDisc = 0, inner = 0, outer = 0, changed = 0;
            for (int i = 0; i < members.Count; i++)
            {
                string[] targetNames;
                string label;
                if (big < BIG_ELDER_SLOTS)
                {
                    targetNames = BigElderNames; label = "BigElder"; big++;
                }
                else if (elder < ELDER_SLOTS)
                {
                    targetNames = ElderNames; label = "Elder"; elder++;
                }
                else if (trueDisc < TRUE_DISCIPLE_SLOTS)
                {
                    targetNames = TrueDiscipleNames; label = "TrueDisciple"; trueDisc++;
                }
                else if (inner < INNER_DISCIPLE_SLOTS)
                {
                    targetNames = InnerDiscipleNames; label = "InnerDisciple"; inner++;
                }
                else
                {
                    targetNames = OuterDiscipleNames; label = "OuterDisciple"; outer++;
                }

                if (SetUnitPostType(members[i].unit, targetNames, label))
                {
                    changed++;
                }
            }

            Log("[RANK] done reason=" + reason
                + " school=" + schoolId
                + " members=" + members.Count
                + " changed=" + changed
                + " slots=" + big + "/" + elder + "/" + trueDisc + "/" + inner + "/" + outer);

            LogTopMembers(members, 8);
        }

        private static List<MemberInfoEx> CollectSchoolMembers(string schoolId, string playerId)
        {
            List<MemberInfoEx> result = new List<MemberInfoEx>();
            HashSet<string> seen = new HashSet<string>();

            try
            {
                var enumerator = g.world.unit.allUnit.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    object current = enumerator.Current;
                    WorldUnitBase unit = ExtractWorldUnit(current);
                    if (unit == null) continue;

                    string uid = GetUnitId(unit);
                    if (string.IsNullOrEmpty(uid)) continue;
                    if (uid == playerId) continue;
                    if (seen.Contains(uid)) continue;

                    dynamic ud = null;
                    try { ud = unit.data.unitData; } catch { continue; }
                    if (ud == null) continue;

                    string sid = SafeString(GetMemberValue(ud, "schoolID"));
                    if (sid != schoolId) continue;
                    if (IsDead(unit, ud)) continue;

                    MemberInfoEx info = new MemberInfoEx();
                    info.unit = unit;
                    info.uid = uid;
                    info.name = GetUnitName(unit, ud);
                    object pd = null;
                    try { pd = unit.data.unitData.propertyData; } catch { }
                    info.grade = Math.Max(
                        GetBestInt(ud, new[] { "grade", "gradeID", "gradeId", "gradeValue", "level", "realm", "realmLevel" }),
                        GetBestInt(pd, new[] { "grade", "gradeID", "gradeId", "gradeValue", "level", "realm", "realmLevel" }));
                    info.phase = Math.Max(
                        GetBestInt(ud, new[] { "phase", "gradePhase", "gradeLevel", "levelPhase", "smallGrade", "stateLevel" }),
                        GetBestInt(pd, new[] { "phase", "gradePhase", "gradeLevel", "levelPhase", "smallGrade", "stateLevel" }));
                    info.power = Math.Max(
                        GetBestInt(ud, new[] { "power", "battlePower", "ability", "fightPower", "combatPower" }),
                        GetBestInt(pd, new[] { "power", "battlePower", "ability", "fightPower", "combatPower" }));
                    info.isTalent = DetectTalent(unit, ud);
                    info.oldPost = SafeString(GetMemberValue(ud, "postType"));

                    seen.Add(uid);
                    result.Add(info);
                }
            }
            catch (Exception ex)
            {
                Log("[COLLECT] failed: " + ex.Message);
            }

            return result;
        }

        private static int CompareMember(MemberInfoEx a, MemberInfoEx b)
        {
            int c = b.grade.CompareTo(a.grade);
            if (c != 0) return c;
            c = b.phase.CompareTo(a.phase);
            if (c != 0) return c;
            c = b.isTalent.CompareTo(a.isTalent);
            if (c != 0) return c;
            c = b.power.CompareTo(a.power);
            if (c != 0) return c;
            return string.Compare(a.name, b.name, StringComparison.Ordinal);
        }

        private static bool SetUnitPostType(WorldUnitBase unit, string[] enumCandidates, string label)
        {
            try
            {
                object ud = unit.data.unitData;
                if (ud == null) return false;
                string uid = GetUnitId(unit);
                object enumValue;
                if (TryResolveSchoolPostEnum(enumCandidates, out enumValue))
                {
                    if (TrySetGlobalPostMapping(uid, enumValue, label)) return true;
                }

                Type t = ud.GetType();

                FieldInfo f = t.GetField("postType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && TrySetEnumOrValue(ud, f.FieldType, v => f.SetValue(ud, v), enumCandidates)) return true;

                PropertyInfo p = t.GetProperty("postType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite && TrySetEnumOrValue(ud, p.PropertyType, v => p.SetValue(ud, v, null), enumCandidates)) return true;

                // 備援欄位名：不同版本可能不是 postType。
                foreach (string memberName in new[] { "schoolPostType", "post", "schoolPost", "schoolStatus" })
                {
                    f = t.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null && TrySetEnumOrValue(ud, f.FieldType, v => f.SetValue(ud, v), enumCandidates)) return true;

                    p = t.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.CanWrite && TrySetEnumOrValue(ud, p.PropertyType, v => p.SetValue(ud, v, null), enumCandidates)) return true;
                }

                Log("[SET] no writable post field for " + GetUnitId(unit) + " target=" + label);
                return false;
            }
            catch (Exception ex)
            {
                Log("[SET] failed target=" + label + " uid=" + GetUnitId(unit) + " err=" + ex.Message);
                return false;
            }
        }

        private static bool TrySetEnumOrValue(object owner, Type valueType, Action<object> setter, string[] enumCandidates)
        {
            try
            {
                Type t = Nullable.GetUnderlyingType(valueType) ?? valueType;
                if (t.IsEnum)
                {
                    object enumValue;
                    if (TryResolveEnumValue(t, enumCandidates, out enumValue))
                    {
                        setter(enumValue);
                        return true;
                    }
                    return false;
                }

                // 若遊戲把職位存成 int，初版不硬猜 enum 值，避免把宗門資料寫壞。
                return false;
            }
            catch (Exception ex)
            {
                Log("[SET] enum set failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryResolveSchoolPostEnum(string[] enumCandidates, out object enumValue)
        {
            enumValue = null;
            try
            {
                Type t = Type.GetType("SchoolPostType");
                if (t == null) t = typeof(WorldUnitBase).Assembly.GetType("SchoolPostType");
                if (t == null || !t.IsEnum) return false;
                return TryResolveEnumValue(t, enumCandidates, out enumValue);
            }
            catch { return false; }
        }

        private static bool TryResolveEnumValue(Type enumType, string[] enumCandidates, out object enumValue)
        {
            enumValue = null;
            try
            {
                string[] names = Enum.GetNames(enumType);
                foreach (string wanted in enumCandidates)
                {
                    foreach (string name in names)
                    {
                        if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                        {
                            enumValue = Enum.Parse(enumType, name);
                            return true;
                        }
                    }
                }
                Log("[SET] enum candidates not found in " + enumType.Name + ": " + string.Join(",", names));
            }
            catch (Exception ex)
            {
                Log("[SET] enum resolve failed: " + ex.Message);
            }
            return false;
        }

        private static bool TrySetGlobalPostMapping(string uid, object enumValue, string label)
        {
            if (string.IsNullOrEmpty(uid) || enumValue == null) return false;
            try
            {
                object unitMgr = g.world.unit;
                foreach (string mapName in new[] { "unitIDToSchoolPostType", "unitIDToSchoolPostTytpe" })
                {
                    object map = GetMemberValue(unitMgr, mapName);
                    if (map == null) continue;
                    if (SetMapValue(map, uid, enumValue))
                    {
                        TryInvokeNoArg(unitMgr, "UpdateUnitIDToSchoolPostTytpe");
                        TryInvokeNoArg(unitMgr, "UpdateUnitIDToSchoolPostType");
                        Log("[SET] " + uid + " -> " + label + " via g.world.unit." + mapName);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[SET] global mapping failed uid=" + uid + " target=" + label + " err=" + ex.Message);
            }
            return false;
        }

        private static bool SetMapValue(object map, string key, object value)
        {
            try
            {
                Type t = map.GetType();
                PropertyInfo item = t.GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (item != null && item.CanWrite)
                {
                    try { item.SetValue(map, value, new object[] { key }); return true; } catch { }
                }

                MethodInfo setItem = t.GetMethod("set_Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (setItem != null)
                {
                    try { setItem.Invoke(map, new object[] { key, value }); return true; } catch { }
                }

                MethodInfo contains = t.GetMethod("ContainsKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo remove = t.GetMethod("Remove", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo add = t.GetMethod("Add", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                bool has = false;
                if (contains != null)
                {
                    object r = contains.Invoke(map, new object[] { key });
                    if (r is bool) has = (bool)r;
                }
                if (has && remove != null) remove.Invoke(map, new object[] { key });
                if (add != null)
                {
                    add.Invoke(map, new object[] { key, value });
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("[SET] map write failed: " + ex.Message);
            }
            return false;
        }

        private static void TryInvokeNoArg(object obj, string methodName)
        {
            try
            {
                if (obj == null) return;
                MethodInfo m = obj.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (m != null && m.GetParameters().Length == 0) m.Invoke(m.IsStatic ? null : obj, null);
            }
            catch { }
        }

        private static void TryDumpSchoolPostTypeEnum()
        {
            try
            {
                Type t = Type.GetType("SchoolPostType");
                if (t == null)
                {
                    Assembly asm = typeof(WorldUnitBase).Assembly;
                    t = asm.GetType("SchoolPostType");
                }
                if (t != null && t.IsEnum)
                {
                    Log("[ENUM] SchoolPostType=" + string.Join(",", Enum.GetNames(t)));
                }
                else
                {
                    Log("[ENUM] SchoolPostType not found yet");
                }
            }
            catch (Exception ex)
            {
                Log("[ENUM] dump failed: " + ex.Message);
            }
        }

        private static bool DetectTalent(WorldUnitBase unit, object ud)
        {
            bool heroByGameApi;
            if (TryCallIsHero(unit, out heroByGameApi)) return heroByGameApi;

            foreach (string name in new[] { "isHero", "isHeroes", "isTalent", "talent", "isTianJiao", "tianJiao", "isHeavenChosen", "isGenius", "genius", "npcHeroes" })
            {
                object v = GetMemberValue(ud, name);
                if (v is bool) return (bool)v;
                if (v != null && v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            }

            // 備援：從稱號/標籤/氣運等資料的文字裡找「天驕」。只當排序加分，不作硬條件。
            string dump = "";
            foreach (string name in new[] { "title", "tag", "tags", "luck", "addLuck", "feature", "features" })
            {
                object v = GetMemberValue(ud, name);
                if (v != null) dump += " " + v;
            }
            return dump.Contains("天骄") || dump.Contains("天驕") || dump.IndexOf("genius", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryCallIsHero(WorldUnitBase unit, out bool result)
        {
            result = false;
            try
            {
                MethodInfo method = ResolveIsHeroMethod();
                if (method == null) return false;
                object value = method.Invoke(null, new object[] { unit });
                if (value is bool)
                {
                    result = (bool)value;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("[HERO] IsHero call failed: " + ex.Message);
            }
            return false;
        }

        private static MethodInfo ResolveIsHeroMethod()
        {
            if (isHeroMethodSearched) return cachedIsHeroMethod;
            isHeroMethodSearched = true;

            try
            {
                Assembly asm = typeof(WorldUnitBase).Assembly;
                foreach (Type t in asm.GetTypes())
                {
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (m.Name != "IsHero" && m.Name != "IsHeroes") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(WorldUnitBase) && m.ReturnType == typeof(bool))
                        {
                            cachedIsHeroMethod = m;
                            Log("[HERO] using " + t.FullName + "." + m.Name + "(WorldUnitBase)");
                            return cachedIsHeroMethod;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[HERO] search failed: " + ex.Message);
            }

            Log("[HERO] IsHero(WorldUnitBase) not found, fallback to fields");
            return null;
        }

        private static bool IsDead(WorldUnitBase unit, object ud)
        {
            foreach (string name in new[] { "isDie", "isDead", "dead" })
            {
                object v = GetMemberValue(ud, name);
                if (v is bool && (bool)v) return true;
            }
            try
            {
                object pd = unit.data.unitData.propertyData;
                object v = GetMemberValue(pd, "isDie");
                if (v is bool && (bool)v) return true;
            }
            catch { }
            return false;
        }

        private static WorldUnitBase ExtractWorldUnit(object current)
        {
            if (current == null) return null;
            WorldUnitBase direct = current as WorldUnitBase;
            if (direct != null) return direct;

            object value = GetMemberValue(current, "Value");
            direct = value as WorldUnitBase;
            if (direct != null) return direct;

            object key = GetMemberValue(current, "Key");
            string uid = key == null ? "" : key.ToString();
            if (!string.IsNullOrEmpty(uid))
            {
                try { return g.world.unit.allUnit[uid]; } catch { }
            }
            return null;
        }

        private static string GetUnitId(WorldUnitBase unit)
        {
            try
            {
                object ud = unit.data.unitData;
                object v = GetMemberValue(ud, "unitID");
                if (v != null) return v.ToString();
                v = GetMemberValue(ud, "id");
                if (v != null) return v.ToString();
            }
            catch { }
            return "";
        }

        private static string GetUnitName(WorldUnitBase unit, object ud)
        {
            try { return unit.data.unitData.propertyData.GetName(); } catch { }
            object v = GetMemberValue(ud, "name");
            return v == null ? GetUnitId(unit) : v.ToString();
        }

        private static int GetBestInt(object obj, string[] names)
        {
            int best = 0;
            foreach (string name in names)
            {
                object v = GetMemberValue(obj, name);
                int parsed;
                if (TryToInt(v, out parsed) && parsed > best) best = parsed;
            }
            return best;
        }

        private static bool TryToInt(object v, out int value)
        {
            value = 0;
            if (v == null) return false;
            if (v is int) { value = (int)v; return true; }
            if (v is long) { value = (int)(long)v; return true; }
            if (v is short) { value = (short)v; return true; }
            if (v is byte) { value = (byte)v; return true; }
            return int.TryParse(v.ToString(), out value);
        }

        private static object GetMemberValue(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                Type t = obj.GetType();
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(obj);
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(obj, null);
            }
            catch { }
            return null;
        }

        private static string SafeString(object v)
        {
            return v == null ? "" : v.ToString();
        }

        private static string GetGameMonthKey()
        {
            object run = null;
            try { run = g.world.run; } catch { }
            if (run != null)
            {
                foreach (string name in new[] { "roundMonth", "gameTime", "curMonth", "month", "round" })
                {
                    object v = GetMemberValue(run, name);
                    if (v != null) return v.ToString();
                }
            }

            // 找不到遊戲月份時才用真實時間備援；這只會影響「多久整編一次」，不影響排序內容。
            return DateTime.Now.ToString("yyyyMMddHHmm");
        }

        private static void LogTopMembers(List<MemberInfoEx> members, int count)
        {
            int n = Math.Min(count, members.Count);
            for (int i = 0; i < n; i++)
            {
                MemberInfoEx m = members[i];
                Log("[TOP] #" + (i + 1) + " " + m.name
                    + " uid=" + m.uid
                    + " grade=" + m.grade
                    + " phase=" + m.phase
                    + " talent=" + m.isTalent
                    + " power=" + m.power
                    + " oldPost=" + m.oldPost);
            }
        }

        private class MemberInfoEx
        {
            public WorldUnitBase unit;
            public string uid;
            public string name;
            public int grade;
            public int phase;
            public int power;
            public bool isTalent;
            public string oldPost;
        }
    }
}
