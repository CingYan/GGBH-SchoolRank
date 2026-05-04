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
        private const string VERSION = "v11";
        private const int CHECK_INTERVAL_FRAMES = 300;

        // 單一宗門實際配額：大長老 2、長老 5、真傳 10、內門 20，其餘為外門。
        private const int BIG_ELDER_SLOTS = 2;
        private const int ELDER_SLOTS = 5;
        private const int TRUE_DISCIPLE_SLOTS = 10;
        private const int INNER_DISCIPLE_SLOTS = 20;

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
        private static bool postStorageProbeDumped = false;
        private static bool branchProbeDumped = false;

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

            DumpBranchProbeOnce(schoolId, playerData, members);

            members.Sort(CompareMember);

            int big = Math.Min(BIG_ELDER_SLOTS, members.Count);
            int elder = Math.Min(ELDER_SLOTS, Math.Max(0, members.Count - big));
            int trueDisc = Math.Min(TRUE_DISCIPLE_SLOTS, Math.Max(0, members.Count - big - elder));
            int inner = Math.Min(INNER_DISCIPLE_SLOTS, Math.Max(0, members.Count - big - elder - trueDisc));
            int outer = Math.Max(0, members.Count - big - elder - trueDisc - inner);
            int changed = 0;

            // V4 正解：鬼谷八荒宗門職位不是 unitData.postType，而是宗門 buildData 裡的名冊清單。
            // 先直接重寫 npcBigElders / npcElders / npcInherit / npcIn，失敗才退回舊的 per-unit 探測。
            if (TryRewriteSchoolRosterLists(schoolId, members))
            {
                changed = members.Count;
            }
            else
            {
                Log("[RANK] roster rewrite failed, fallback to per-unit post probing");
                int ib = 0, ie = 0, it = 0, ii = 0;
                for (int i = 0; i < members.Count; i++)
                {
                    string[] targetNames;
                    string label;
                    if (ib < BIG_ELDER_SLOTS)
                    {
                        targetNames = BigElderNames; label = "BigElder"; ib++;
                    }
                    else if (ie < ELDER_SLOTS)
                    {
                        targetNames = ElderNames; label = "Elder"; ie++;
                    }
                    else if (it < TRUE_DISCIPLE_SLOTS)
                    {
                        targetNames = TrueDiscipleNames; label = "TrueDisciple"; it++;
                    }
                    else if (ii < INNER_DISCIPLE_SLOTS)
                    {
                        targetNames = InnerDiscipleNames; label = "InnerDisciple"; ii++;
                    }
                    else
                    {
                        targetNames = OuterDiscipleNames; label = "OuterDisciple";
                    }

                    if (SetUnitPostType(members[i].unit, targetNames, label)) changed++;
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

        private static void DumpBranchProbeOnce(string schoolId, object playerUnitData, List<MemberInfoEx> members)
        {
            if (branchProbeDumped) return;
            branchProbeDumped = true;

            try
            {
                Log("[BRANCH-PROBE] start school=" + schoolId + " members=" + members.Count + " mode=read-only");
                DumpBranchLikeMembers(playerUnitData, "player.unitData", 0);

                object playerData = null;
                try { playerData = g.world.playerUnit.data; } catch { }
                foreach (string schoolMember in new[] { "school", "_school" })
                {
                    object schoolWrap = GetMemberValue(playerData, schoolMember);
                    DumpBranchLikeMembers(schoolWrap, "player.data." + schoolMember, 0);
                    DumpBranchLikeMembers(GetMemberValue(schoolWrap, "buildData"), "player.data." + schoolMember + ".buildData", 0);
                }

                int sampleCount = Math.Min(12, members.Count);
                for (int i = 0; i < sampleCount; i++)
                {
                    MemberInfoEx m = members[i];
                    object ud = null;
                    object pd = null;
                    try { ud = m.unit.data.unitData; } catch { }
                    try { pd = m.unit.data.unitData.propertyData; } catch { }
                    Log("[BRANCH-PROBE] npc #" + (i + 1) + " name=" + m.name + " uid=" + m.uid + " schoolID=" + SafeString(GetMemberValue(ud, "schoolID")));
                    DumpBranchLikeMembers(ud, "npc[" + i + "].unitData", 0);
                    DumpBranchLikeMembers(pd, "npc[" + i + "].propertyData", 0);
                }

                int hits = 0;
                HashSet<int> seen = new HashSet<int>();
                ScanSchoolBranchCandidates(g.world, "g.world", 0, seen, ref hits);
                try { ScanSchoolBranchCandidates(g.data, "g.data", 0, seen, ref hits); } catch { }
                Log("[BRANCH-PROBE] done candidateHits=" + hits);
            }
            catch (Exception ex)
            {
                Log("[BRANCH-PROBE] failed: " + ex.Message);
            }
        }

        private static void DumpBranchLikeMembers(object obj, string path, int depth)
        {
            if (obj == null || depth > 1) return;
            try
            {
                Type t = obj.GetType();
                if (IsTerminalType(t)) return;
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                foreach (FieldInfo f in t.GetFields(flags))
                {
                    if (!IsBranchProbeName(f.Name)) continue;
                    object v = null;
                    try { v = f.GetValue(obj); } catch { }
                    LogBranchProbeValue(path + "." + f.Name, f.FieldType, v, depth);
                }
                foreach (PropertyInfo p in t.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    if (!IsBranchProbeName(p.Name)) continue;
                    object v = null;
                    try { v = p.GetValue(obj, null); } catch { }
                    LogBranchProbeValue(path + "." + p.Name, p.PropertyType, v, depth);
                }
            }
            catch (Exception ex)
            {
                Log("[BRANCH-PROBE] dump failed " + path + ": " + ex.Message);
            }
        }

        private static void LogBranchProbeValue(string path, Type declaredType, object value, int depth)
        {
            try
            {
                string valueType = value == null ? "null" : value.GetType().FullName;
                int count;
                if (TryGetCollectionCount(value, out count))
                {
                    Log("[BRANCH-PROBE] list " + path + " declared=" + declaredType.FullName + " valueType=" + valueType + " count=" + count + " sample=" + SampleCollection(value, count, 5));
                    return;
                }

                string sample = value == null ? "null" : value.ToString();
                if (sample.Length > 140) sample = sample.Substring(0, 140);
                Log("[BRANCH-PROBE] member " + path + " declared=" + declaredType.FullName + " valueType=" + valueType + " value=" + sample);

                if (value != null && depth < 1 && !IsTerminalType(value.GetType())) DumpBranchLikeMembers(value, path, depth + 1);
            }
            catch { }
        }

        private static bool IsBranchProbeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("school") || n.Contains("sect") || n.Contains("branch") || n.Contains("sub")
                || n.Contains("hall") || n.Contains("area") || n.Contains("build") || n.Contains("parent")
                || n.Contains("data") || n.Contains("name") || n.Contains("npc") || n.Contains("elder") || n.Contains("inherit")
                || n.Contains("member") || n.Contains("post") || n.Contains("position") || n.Contains("duty");
        }

        private static void ScanSchoolBranchCandidates(object obj, string path, int depth, HashSet<int> seen, ref int hits)
        {
            if (obj == null || depth > 4 || hits >= 80) return;
            try
            {
                Type t = obj.GetType();
                if (IsTerminalType(t)) return;
                int hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
                if (seen.Contains(hash)) return;
                seen.Add(hash);

                if (LooksLikeBranchSchoolCandidate(obj))
                {
                    hits++;
                    LogSchoolBranchCandidate(obj, path, hits);
                }

                System.Collections.IEnumerable en = obj as System.Collections.IEnumerable;
                if (en != null && !(obj is string))
                {
                    int i = 0;
                    foreach (object item in en)
                    {
                        object candidate = item;
                        object val = GetMemberValue(item, "Value");
                        if (val != null) candidate = val;
                        ScanSchoolBranchCandidates(candidate, path + "[]", depth + 1, seen, ref hits);
                        if (++i > 300) break;
                        if (hits >= 80) break;
                    }
                    return;
                }

                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                foreach (FieldInfo f in t.GetFields(flags))
                {
                    if (!IsBranchProbeName(f.Name)) continue;
                    object v = null;
                    try { v = f.GetValue(obj); } catch { }
                    ScanSchoolBranchCandidates(v, path + "." + f.Name, depth + 1, seen, ref hits);
                    if (hits >= 80) return;
                }
                foreach (PropertyInfo p in t.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    if (!IsBranchProbeName(p.Name)) continue;
                    object v = null;
                    try { v = p.GetValue(obj, null); } catch { }
                    ScanSchoolBranchCandidates(v, path + "." + p.Name, depth + 1, seen, ref hits);
                    if (hits >= 80) return;
                }
            }
            catch { }
        }

        private static bool LooksLikeBranchSchoolCandidate(object obj)
        {
            if (obj == null) return false;
            if (HasMember(obj, "npcIn") || HasMember(obj, "npcOut") || HasMember(obj, "npcElders") || HasMember(obj, "npcBigElders") || HasMember(obj, "npcInherit")) return true;
            if (HasMember(obj, "schoolID") && (HasMember(obj, "name") || HasMember(obj, "schoolName") || HasMember(obj, "buildData") || HasMember(obj, "parentID") || HasMember(obj, "branchID") || HasMember(obj, "hallID"))) return true;
            return false;
        }

        private static void LogSchoolBranchCandidate(object obj, string path, int index)
        {
            try
            {
                string id = FirstNonEmpty(obj, new[] { "id", "schoolID", "schoolId", "buildID", "buildId" });
                string name = FirstNonEmpty(obj, new[] { "name", "schoolName", "buildName", "areaName", "branchName", "hallName" });
                string parent = FirstNonEmpty(obj, new[] { "parentID", "parentId", "parentSchoolID", "parentSchoolId", "mainSchoolID", "mainSchoolId" });
                string branch = FirstNonEmpty(obj, new[] { "branchID", "branchId", "subSchoolID", "subSchoolId", "hallID", "hallId", "areaID", "areaId" });
                Log("[BRANCH-PROBE] candidate #" + index + " path=" + path + " type=" + obj.GetType().FullName
                    + " id=" + id + " name=" + name + " parent=" + parent + " branch=" + branch
                    + " roster=" + RosterCounts(obj));
            }
            catch (Exception ex)
            {
                Log("[BRANCH-PROBE] candidate log failed " + path + ": " + ex.Message);
            }
        }

        private static string FirstNonEmpty(object obj, string[] names)
        {
            foreach (string name in names)
            {
                string value = SafeString(GetMemberValue(obj, name));
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return "";
        }

        private static string RosterCounts(object obj)
        {
            List<string> parts = new List<string>();
            foreach (string name in new[] { "npcBigElders", "npcElders", "npcInherit", "npcIn", "npcOut", "npcOuter", "members", "npcMembers" })
            {
                object v = GetMemberValue(obj, name);
                int count;
                if (TryGetCollectionCount(v, out count)) parts.Add(name + "=" + count);
            }
            return string.Join(",", parts.ToArray());
        }


        private static bool TryRewriteSchoolRosterLists(string schoolId, List<MemberInfoEx> members)
        {
            object schoolData = FindSchoolBuildDataById(schoolId);
            if (schoolData == null)
            {
                Log("[ROSTER] school buildData not found for school=" + schoolId);
                return false;
            }

            try
            {
                List<MemberInfoEx> big = SliceMembers(members, 0, BIG_ELDER_SLOTS);
                List<MemberInfoEx> elders = SliceMembers(members, BIG_ELDER_SLOTS, ELDER_SLOTS);
                List<MemberInfoEx> inherit = SliceMembers(members, BIG_ELDER_SLOTS + ELDER_SLOTS, TRUE_DISCIPLE_SLOTS);
                List<MemberInfoEx> inner = SliceMembers(members, BIG_ELDER_SLOTS + ELDER_SLOTS + TRUE_DISCIPLE_SLOTS, INNER_DISCIPLE_SLOTS);
                List<MemberInfoEx> outer = SliceMembers(members, BIG_ELDER_SLOTS + ELDER_SLOTS + TRUE_DISCIPLE_SLOTS + INNER_DISCIPLE_SLOTS, 9999);

                bool okBig = SetRosterList(schoolData, new[] { "npcBigElders", "npcBigElder", "bigElders", "bigElder" }, big, "BigElders");
                bool okElder = SetRosterList(schoolData, new[] { "npcElders", "npcElder", "elders", "elder" }, elders, "Elders");
                bool okInherit = SetRosterList(schoolData, new[] { "npcInherit", "npcInherits", "inheres", "inherit", "npcTrueDisciple", "npcTrueDisciples" }, inherit, "Inherit");

                // 內門 + 外門在很多版本只是一個 npcIn 清單；若有 npcOut 則分開寫。
                bool hasOuter = HasMember(schoolData, "npcOut") || HasMember(schoolData, "npcOuter") || HasMember(schoolData, "npcOuts");
                bool okInner;
                bool okOuter = true;
                if (hasOuter)
                {
                    okInner = SetRosterList(schoolData, new[] { "npcIn", "npcInner", "npcInners", "inner" }, inner, "Inner");
                    okOuter = SetRosterList(schoolData, new[] { "npcOut", "npcOuter", "npcOuts", "outer" }, outer, "Outer");
                }
                else
                {
                    List<MemberInfoEx> rest = new List<MemberInfoEx>();
                    rest.AddRange(inner);
                    rest.AddRange(outer);
                    okInner = SetRosterList(schoolData, new[] { "npcIn", "npcInner", "npcInners", "inner" }, rest, "In+Outer");
                }

                ForceRefreshSchoolObjects(schoolData);
                DumpSchoolDebugObjects(schoolData, "after-write");

                Log("[ROSTER] rewrite school=" + schoolId
                    + " ok=" + okBig + "/" + okElder + "/" + okInherit + "/" + okInner + "/" + okOuter
                    + " counts=" + big.Count + "/" + elders.Count + "/" + inherit.Count + "/" + inner.Count + "/" + outer.Count);
                return okBig || okElder || okInherit || okInner || okOuter;
            }
            catch (Exception ex)
            {
                Log("[ROSTER] rewrite failed: " + ex);
                return false;
            }
        }

        private static List<MemberInfoEx> SliceMembers(List<MemberInfoEx> members, int start, int count)
        {
            List<MemberInfoEx> result = new List<MemberInfoEx>();
            for (int i = start; i < members.Count && result.Count < count; i++) result.Add(members[i]);
            return result;
        }

        private static bool SetRosterList(object schoolData, string[] names, List<MemberInfoEx> selected, string label)
        {
            foreach (string name in names)
            {
                object list = GetMemberValue(schoolData, name);
                if (list == null) continue;
                if (ReplaceListContentsSmart(list, selected, label + "." + name))
                {
                    Log("[ROSTER] " + label + " -> " + name + " count=" + selected.Count + " top=" + FirstNames(selected, 3));
                    return true;
                }
            }
            Log("[ROSTER] missing list for " + label);
            return false;
        }

        private static bool ReplaceListContentsSmart(object list, List<MemberInfoEx> selected, string label)
        {
            try
            {
                Type t = list.GetType();
                MethodInfo clear = t.GetMethod("Clear", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo add = t.GetMethod("Add", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (clear == null || add == null) return false;

                Type itemType = GetListItemType(list);
                Log("[ROSTER] " + label + " listType=" + t.FullName + " itemType=" + (itemType == null ? "unknown" : itemType.FullName));

                clear.Invoke(list, null);
                foreach (MemberInfoEx m in selected)
                {
                    object item = ConvertRosterItem(m, itemType);
                    add.Invoke(list, new object[] { item });
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("[ROSTER] list replace failed " + label + ": " + ex.Message);
                return false;
            }
        }

        private static Type GetListItemType(object list)
        {
            try
            {
                Type t = list.GetType();
                if (t.IsGenericType)
                {
                    Type[] args = t.GetGenericArguments();
                    if (args.Length == 1) return args[0];
                }
                MethodInfo add = t.GetMethod("Add", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (add != null)
                {
                    ParameterInfo[] ps = add.GetParameters();
                    if (ps.Length == 1) return ps[0].ParameterType;
                }
            }
            catch { }
            return typeof(string);
        }

        private static object ConvertRosterItem(MemberInfoEx m, Type itemType)
        {
            if (itemType == null || itemType == typeof(string) || itemType == typeof(object)) return m.uid;
            try
            {
                if (itemType.IsAssignableFrom(typeof(WorldUnitBase))) return m.unit;
                object ud = m.unit.data.unitData;
                if (ud != null && itemType.IsAssignableFrom(ud.GetType())) return ud;
                object pd = m.unit.data.unitData.propertyData;
                if (pd != null && itemType.IsAssignableFrom(pd.GetType())) return pd;
            }
            catch { }
            return m.uid;
        }

        private static string FirstNames(List<MemberInfoEx> members, int count)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < members.Count && i < count; i++) names.Add(members[i].name + "(" + members[i].uid + ")");
            return string.Join(",", names.ToArray());
        }


        private static void DumpSchoolDebugObjects(object schoolData, string reason)
        {
            try
            {
                Log("[DUMP-LISTS] reason=" + reason);
                DumpObjectMembers(schoolData, "buildData", 0);
                object schoolWrap = null;
                try { schoolWrap = GetMemberValue(g.world.playerUnit.data, "school"); } catch { }
                if (schoolWrap == null) { try { schoolWrap = GetMemberValue(g.world.playerUnit.data, "_school"); } catch { } }
                DumpObjectMembers(schoolWrap, "playerUnit.data.school", 0);
            }
            catch (Exception ex)
            {
                Log("[DUMP-LISTS] failed: " + ex.Message);
            }
        }

        private static void DumpObjectMembers(object obj, string path, int depth)
        {
            if (obj == null || depth > 1) return;
            try
            {
                Type t = obj.GetType();
                Log("[DUMP-LISTS] object " + path + " type=" + t.FullName);
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                foreach (FieldInfo f in t.GetFields(flags))
                {
                    object v = null;
                    try { v = f.GetValue(obj); } catch { }
                    DumpMemberValue(path + "." + f.Name, f.FieldType, v, depth);
                }
                foreach (PropertyInfo p in t.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    object v = null;
                    try { v = p.GetValue(obj, null); } catch { }
                    DumpMemberValue(path + "." + p.Name, p.PropertyType, v, depth);
                }
            }
            catch (Exception ex)
            {
                Log("[DUMP-LISTS] object dump failed " + path + ": " + ex.Message);
            }
        }

        private static void DumpMemberValue(string path, Type declaredType, object value, int depth)
        {
            try
            {
                string lower = path.ToLowerInvariant();
                bool interestingName = lower.Contains("npc") || lower.Contains("unit") || lower.Contains("elder") || lower.Contains("school") || lower.Contains("post") || lower.Contains("hall") || lower.Contains("build") || lower.Contains("member") || lower.Contains("office") || lower.Contains("job") || lower.Contains("position") || lower.Contains("duty");
                string valueType = value == null ? "null" : value.GetType().FullName;

                int count;
                if (TryGetCollectionCount(value, out count))
                {
                    Log("[DUMP-LISTS] list " + path + " declared=" + declaredType.FullName + " valueType=" + valueType + " count=" + count + " sample=" + SampleCollection(value, count, 8));
                    return;
                }

                if (interestingName)
                {
                    string sample = value == null ? "null" : value.ToString();
                    if (sample.Length > 120) sample = sample.Substring(0, 120);
                    Log("[DUMP-LISTS] member " + path + " declared=" + declaredType.FullName + " valueType=" + valueType + " value=" + sample);
                }

                if (value != null && depth < 1 && interestingName && !IsTerminalType(value.GetType()))
                {
                    DumpObjectMembers(value, path, depth + 1);
                }
            }
            catch (Exception ex)
            {
                Log("[DUMP-LISTS] member failed " + path + ": " + ex.Message);
            }
        }

        private static bool TryGetCollectionCount(object value, out int count)
        {
            count = 0;
            if (value == null || value is string) return false;
            try
            {
                Type t = value.GetType();
                PropertyInfo p = t.GetProperty("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null) p = t.GetProperty("Length", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null)
                {
                    object c = p.GetValue(value, null);
                    return TryToInt(c, out count);
                }
                FieldInfo f = t.GetField("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null)
                {
                    object c = f.GetValue(value);
                    return TryToInt(c, out count);
                }
            }
            catch { }
            return false;
        }

        private static string SampleCollection(object value, int count, int max)
        {
            List<string> sample = new List<string>();
            if (value == null) return "";
            Type t = value.GetType();
            PropertyInfo itemProp = t.GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo getItem = t.GetMethod("get_Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < count && i < max; i++)
            {
                object item = null;
                try
                {
                    if (itemProp != null) item = itemProp.GetValue(value, new object[] { i });
                    else if (getItem != null) item = getItem.Invoke(value, new object[] { i });
                }
                catch { }
                sample.Add(DescribeListItem(item));
            }
            return string.Join(" | ", sample.ToArray());
        }

        private static string DescribeListItem(object item)
        {
            if (item == null) return "null";
            try
            {
                string s = item.ToString();
                WorldUnitBase unit = null;
                if (item is WorldUnitBase) unit = (WorldUnitBase)item;
                else if (!string.IsNullOrEmpty(s))
                {
                    try { unit = g.world.unit.allUnit[s]; } catch { }
                }
                if (unit != null)
                {
                    object ud = unit.data.unitData;
                    return GetUnitName(unit, ud) + "(" + GetUnitId(unit) + ")";
                }
                if (s.Length > 80) s = s.Substring(0, 80);
                return s;
            }
            catch { return item.ToString(); }
        }

        private static void ForceRefreshSchoolObjects(object schoolData)
        {
            // v5 的 aggressive Refresh/Update/Init 可能在宗門 UI 開啟期間觸發遊戲 UI lifecycle NRE：
            // UnitActionFeedback1012.OnCreate() NullReferenceException。
            // v6 改成只呼叫已知的 unit post cache 更新；畫面請關閉宗門頁後重開或切場景讓 UI 自然重建。
            try
            {
                TryInvokeNoArg(g.world.unit, "UpdateUnitIDToSchoolPostTytpe");
                TryInvokeNoArg(g.world.unit, "UpdateUnitIDToSchoolPostType");
            }
            catch (Exception ex)
            {
                Log("[ROSTER] safe refresh failed: " + ex.Message);
            }
        }

        private static bool HasMember(object obj, string name)
        {
            if (obj == null) return false;
            Type t = obj.GetType();
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            return t.GetField(name, flags) != null || t.GetProperty(name, flags) != null;
        }

        private static object FindSchoolBuildDataById(string schoolId)
        {
            // v11：先走實測已知正解路徑。v6/v7 已證明資料在 playerUnit.data.school/_school.buildData。
            // 不要先全域掃描；Il2Cpp wrapper 物件圖很容易因 depth/seen/初始化狀態漏掉。
            try
            {
                object playerData = g.world.playerUnit.data;
                foreach (string schoolMember in new[] { "school", "_school" })
                {
                    object schoolWrap = GetMemberValue(playerData, schoolMember);
                    object buildData = GetMemberValue(schoolWrap, "buildData");
                    if (LooksLikeSchoolBuildData(buildData, schoolId))
                    {
                        Log("[ROSTER] found schoolData at g.world.playerUnit.data." + schoolMember + ".buildData type=" + buildData.GetType().FullName);
                        return buildData;
                    }
                    // 有些版本 schoolWrap 本身就是 SchoolData。
                    if (LooksLikeSchoolBuildData(schoolWrap, schoolId))
                    {
                        Log("[ROSTER] found schoolData at g.world.playerUnit.data." + schoolMember + " type=" + schoolWrap.GetType().FullName);
                        return schoolWrap;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[ROSTER] direct player school lookup failed: " + ex.Message);
            }

            object direct = TryFindSchoolDataInObject(g.world, schoolId, "g.world", 0, new HashSet<int>());
            if (direct != null) return direct;
            try { return TryFindSchoolDataInObject(g.data, schoolId, "g.data", 0, new HashSet<int>()); } catch { }
            return null;
        }

        private static object TryFindSchoolDataInObject(object obj, string schoolId, string path, int depth, HashSet<int> seen)
        {
            if (obj == null || depth > 5) return null;
            try
            {
                int hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
                if (seen.Contains(hash)) return null;
                seen.Add(hash);

                if (LooksLikeSchoolBuildData(obj, schoolId))
                {
                    Log("[ROSTER] found schoolData at " + path + " type=" + obj.GetType().FullName);
                    return obj;
                }

                Type t = obj.GetType();
                if (IsTerminalType(t)) return null;
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                foreach (FieldInfo f in t.GetFields(flags))
                {
                    object v = null;
                    try { v = f.GetValue(obj); } catch { }
                    object found = TryFindSchoolDataChild(v, schoolId, path + "." + f.Name, depth + 1, seen);
                    if (found != null) return found;
                }
                foreach (PropertyInfo p in t.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    object v = null;
                    try { v = p.GetValue(obj, null); } catch { }
                    object found = TryFindSchoolDataChild(v, schoolId, path + "." + p.Name, depth + 1, seen);
                    if (found != null) return found;
                }
            }
            catch (Exception ex)
            {
                if (depth <= 1) Log("[ROSTER] scan failed at " + path + ": " + ex.Message);
            }
            return null;
        }

        private static object TryFindSchoolDataChild(object v, string schoolId, string path, int depth, HashSet<int> seen)
        {
            if (v == null) return null;
            Type vt = v.GetType();
            if (IsTerminalType(vt)) return null;
            try
            {
                System.Collections.IEnumerable en = v as System.Collections.IEnumerable;
                if (en != null && !(v is string))
                {
                    int i = 0;
                    foreach (object item in en)
                    {
                        object candidate = item;
                        object val = GetMemberValue(item, "Value");
                        if (val != null) candidate = val;
                        object found = TryFindSchoolDataInObject(candidate, schoolId, path + "[]", depth + 1, seen);
                        if (found != null) return found;
                        if (++i > 2000) break;
                    }
                    return null;
                }
            }
            catch { }
            return TryFindSchoolDataInObject(v, schoolId, path, depth, seen);
        }

        private static bool LooksLikeSchoolBuildData(object obj, string schoolId)
        {
            if (obj == null) return false;
            string id = SafeString(GetMemberValue(obj, "id"));
            if (id != schoolId)
            {
                string sid = SafeString(GetMemberValue(obj, "schoolID"));
                if (sid != schoolId) return false;
            }
            return HasMember(obj, "npcIn") || HasMember(obj, "npcElders") || HasMember(obj, "npcBigElders") || HasMember(obj, "npcInherit");
        }

        private static bool IsTerminalType(Type t)
        {
            if (t == null) return true;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t.FullName.StartsWith("System.Reflection");
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

                object postEnumValue;
                if (TryResolveSchoolPostEnum(enumCandidates, out postEnumValue))
                {
                    if (TrySetPostLikeMemberRecursive(unit, postEnumValue, "unit", 0)) return true;
                    if (TrySetPostLikeMemberRecursive(ud, postEnumValue, "unitData", 0)) return true;
                }

                DumpPostStorageProbeOnce(unit);
                Log("[SET] no writable post field for " + GetUnitId(unit) + " target=" + label);
                return false;
            }
            catch (Exception ex)
            {
                Log("[SET] failed target=" + label + " uid=" + GetUnitId(unit) + " err=" + ex.Message);
                return false;
            }
        }


        private static bool TrySetPostLikeMemberRecursive(object obj, object enumValue, string path, int depth)
        {
            if (obj == null || enumValue == null || depth > 2) return false;
            try
            {
                Type t = obj.GetType();
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                foreach (FieldInfo f in t.GetFields(flags))
                {
                    if (IsPostLikeName(f.Name) && TrySetPostValue(obj, f.FieldType, v => f.SetValue(obj, v), enumValue, path + "." + f.Name)) return true;
                }

                foreach (PropertyInfo p in t.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    if (IsPostLikeName(p.Name) && p.CanWrite && TrySetPostValue(obj, p.PropertyType, v => p.SetValue(obj, v, null), enumValue, path + "." + p.Name)) return true;
                }
            }
            catch (Exception ex)
            {
                Log("[SET] post-like recursive failed at " + path + ": " + ex.Message);
            }
            return false;
        }

        private static bool IsPostLikeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("post") || n.Contains("position") || n.Contains("duty");
        }

        private static bool TrySetPostValue(object owner, Type valueType, Action<object> setter, object enumValue, string path)
        {
            try
            {
                Type vt = Nullable.GetUnderlyingType(valueType) ?? valueType;
                object value = null;
                if (vt.IsEnum)
                {
                    if (vt != enumValue.GetType()) return false;
                    value = enumValue;
                }
                else if (vt == typeof(int)) value = Convert.ToInt32(enumValue);
                else if (vt == typeof(long)) value = Convert.ToInt64(enumValue);
                else if (vt == typeof(short)) value = Convert.ToInt16(enumValue);
                else return false;

                setter(value);
                Log("[SET] post-like member hit " + path + " type=" + valueType.FullName);
                return true;
            }
            catch (Exception ex)
            {
                Log("[SET] post-like member failed " + path + ": " + ex.Message);
                return false;
            }
        }

        private static void DumpPostStorageProbeOnce(WorldUnitBase unit)
        {
            if (postStorageProbeDumped) return;
            postStorageProbeDumped = true;
            try
            {
                Log("[PROBE-POST] unitData members containing post/school:");
                object ud = unit.data.unitData;
                DumpInterestingMembers(ud, "unitData");
                Log("[PROBE-POST] g.world.unit members containing post/school:");
                DumpInterestingMembers(g.world.unit, "g.world.unit");
            }
            catch (Exception ex)
            {
                Log("[PROBE-POST] failed: " + ex.Message);
            }
        }

        private static void DumpInterestingMembers(object obj, string path)
        {
            if (obj == null) return;
            Type t = obj.GetType();
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (FieldInfo f in t.GetFields(flags))
            {
                string n = f.Name.ToLowerInvariant();
                if (n.Contains("post") || n.Contains("school")) Log("[PROBE-POST] field " + path + "." + f.Name + " type=" + f.FieldType.FullName);
            }
            foreach (PropertyInfo p in t.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                string n = p.Name.ToLowerInvariant();
                if (n.Contains("post") || n.Contains("school")) Log("[PROBE-POST] prop " + path + "." + p.Name + " type=" + p.PropertyType.FullName + " canWrite=" + p.CanWrite);
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

                // 常見/疑似欄位名先直打。上一版只試兩個名字太保守，實測已證明不夠。
                foreach (string mapName in new[] {
                    "unitIDToSchoolPostType", "unitIDToSchoolPostTytpe",
                    "unitIdToSchoolPostType", "unitIdToSchoolPostTytpe",
                    "unitIDToPostType", "unitIdToPostType",
                    "unitID2SchoolPostType", "unitID2SchoolPostTytpe",
                    "schoolPostType", "schoolPostTypes", "schoolPosts", "postTypes"
                })
                {
                    object map = GetMemberValue(unitMgr, mapName);
                    if (map == null) continue;
                    if (SetMapValue(map, uid, enumValue))
                    {
                        FlushPostCaches(unitMgr);
                        Log("[SET] " + uid + " -> " + label + " via g.world.unit." + mapName);
                        return true;
                    }
                }

                // V3：掃描 g.world.unit 內所有「像職位表」的 Dictionary / Map。
                // 限定 value type 必須能吃 SchoolPostType enum，避免亂寫其他資料表。
                if (TrySetPostMapByReflection(unitMgr, uid, enumValue, "g.world.unit", 0))
                {
                    FlushPostCaches(unitMgr);
                    Log("[SET] " + uid + " -> " + label + " via reflected post map");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("[SET] global mapping failed uid=" + uid + " target=" + label + " err=" + ex.Message);
            }
            return false;
        }

        private static bool TrySetPostMapByReflection(object obj, string uid, object enumValue, string path, int depth)
        {
            if (obj == null || depth > 2) return false;
            try
            {
                Type t = obj.GetType();
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                foreach (FieldInfo f in t.GetFields(flags))
                {
                    object value = null;
                    try { value = f.GetValue(obj); } catch { }
                    if (TrySetCandidatePostMap(value, uid, enumValue, path + "." + f.Name)) return true;
                }

                foreach (PropertyInfo p in t.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    object value = null;
                    try { value = p.GetValue(obj, null); } catch { }
                    if (TrySetCandidatePostMap(value, uid, enumValue, path + "." + p.Name)) return true;
                }
            }
            catch (Exception ex)
            {
                Log("[SET] post map reflection failed at " + path + ": " + ex.Message);
            }
            return false;
        }

        private static bool TrySetCandidatePostMap(object map, string uid, object enumValue, string path)
        {
            if (map == null) return false;
            try
            {
                Type t = map.GetType();
                if (!LooksLikeMap(t)) return false;

                Type valueType = GetMapValueType(t);
                if (valueType != null)
                {
                    Type vt = Nullable.GetUnderlyingType(valueType) ?? valueType;
                    Type et = enumValue.GetType();
                    if (vt.IsEnum && vt != et) return false;
                    if (!vt.IsEnum && vt != typeof(object) && vt != typeof(int) && vt != typeof(long) && vt != typeof(short)) return false;
                    if (!vt.IsEnum && !IsPostLikeName(path)) return false;
                }

                object valueToSet = ConvertPostValueForMap(enumValue, valueType);
                if (SetMapValue(map, uid, valueToSet))
                {
                    Log("[SET] reflected map hit " + path + " type=" + t.FullName);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("[SET] candidate map failed " + path + ": " + ex.Message);
            }
            return false;
        }

        private static bool LooksLikeMap(Type t)
        {
            if (t == null) return false;
            if (t.GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) == null) return false;
            return t.GetMethod("ContainsKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null
                || t.GetMethod("Add", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null
                || t.FullName.IndexOf("Dictionary", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Type GetMapValueType(Type t)
        {
            try
            {
                if (t.IsGenericType)
                {
                    Type[] args = t.GetGenericArguments();
                    if (args.Length == 2) return args[1];
                }

                PropertyInfo item = t.GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (item != null) return item.PropertyType;
            }
            catch { }
            return null;
        }

        private static object ConvertPostValueForMap(object enumValue, Type valueType)
        {
            if (valueType == null) return enumValue;
            Type vt = Nullable.GetUnderlyingType(valueType) ?? valueType;
            try
            {
                if (vt.IsEnum) return enumValue;
                if (vt == typeof(int)) return Convert.ToInt32(enumValue);
                if (vt == typeof(long)) return Convert.ToInt64(enumValue);
                if (vt == typeof(short)) return Convert.ToInt16(enumValue);
            }
            catch { }
            return enumValue;
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

        private static void FlushPostCaches(object unitMgr)
        {
            TryInvokeNoArg(unitMgr, "UpdateUnitIDToSchoolPostTytpe");
            TryInvokeNoArg(unitMgr, "UpdateUnitIDToSchoolPostType");
            TryInvokeNoArg(unitMgr, "UpdateSchoolPostType");
            TryInvokeNoArg(unitMgr, "UpdateSchoolPost");
            TryInvokeNoArg(unitMgr, "UpdateUnitPost");
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
