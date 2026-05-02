using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using HarmonyLib;

namespace MOD_nV039M
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

        private static readonly string[] BigElderNames = new[] { "SchoolBigElder", "BigElder", "bigElder", "WarBigElder" };
        private static readonly string[] ElderNames = new[] { "SchoolElder", "Elder", "elder", "WarElder" };
        private static readonly string[] TrueDiscipleNames = new[] { "SchoolInherit", "Inherit", "inherit", "Disciple", "SchoolDisciple" };
        private static readonly string[] InnerDiscipleNames = new[] { "SchoolInner", "Inner", "inner", "SchoolInnerDisciple", "InnerDisciple" };
        private static readonly string[] OuterDiscipleNames = new[] { "SchoolOuter", "Outer", "outer", "SchoolOuterDisciple", "OuterDisciple" };

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
                    WorldUnitBase unit = enumerator.Current;
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
                    info.grade = GetBestInt(ud, new[] { "grade", "gradeID", "gradeId", "gradeValue", "level", "realm", "realmLevel" });
                    info.phase = GetBestInt(ud, new[] { "phase", "gradePhase", "gradeLevel", "levelPhase", "smallGrade", "stateLevel" });
                    info.power = GetBestInt(ud, new[] { "power", "battlePower", "ability", "fightPower", "combatPower" });
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
                    string[] names = Enum.GetNames(t);
                    foreach (string wanted in enumCandidates)
                    {
                        foreach (string name in names)
                        {
                            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                            {
                                setter(Enum.Parse(t, name));
                                return true;
                            }
                        }
                    }
                    Log("[SET] enum candidates not found in " + t.Name + ": " + string.Join(",", names));
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
            foreach (string name in new[] { "isTalent", "talent", "isTianJiao", "tianJiao", "isHeavenChosen", "isGenius", "genius" })
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

        private static bool IsDead(WorldUnitBase unit, object ud)
        {
            foreach (string name in new[] { "isDie", "isDead", "dead" })
            {
                object v = GetMemberValue(ud, name);
                if (v is bool && (bool)v) return true;
            }
            try { if (unit.data.unitData.propertyData.isDie) return true; } catch { }
            return false;
        }

        private static string GetUnitId(WorldUnitBase unit)
        {
            try { return unit.data.unitData.unitID; } catch { }
            try { return unit.data.unitData.id; } catch { }
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
