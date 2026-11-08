-- Run after Connect_Max.ms. The fixture restores the session, selection and dirty flag.
-- No scene reset, file export, or changes to the artist's existing objects.
global kcBridgeRunPreviewTests
fn kcBridgeRunPreviewTests reportPath = (
    local previous = #(kcBridgeSessionDirectory, kcBridgeSessionId, kcBridgeNodes, kcBridgeSceneBaseline)
    local selected = selection as array
    local dirty = getSaveRequired()
    local created = #(), results = #(), failure = undefined
    local folder = (getDir #temp) + "/Unity3dsMaxBridgePreview_" + kcBridgeGuid()
    makeDir folder all:true
    local registry = folder + "/UpdateTargets.txt"
    local writeRegistry
    fn writeRegistry path applied:false complete:true = (
        local stream = createFile path encoding:#utf8
        format "UMB_UPDATE_TARGETS_1\nnew-directory|Assets/Preview Models\nknown|fixture-source\nsource|fixture-source|Assets/Preview Models/Original.fbx\n" to:stream
        if applied do format "known|fixture-applied\nsource|fixture-applied|Assets/Preview Models/Added.fbx\n" to:stream
        if complete do format "UMB_END\n" to:stream
        close stream
    )
    try (
        kcBridgeSessionId = "preview-test"
        kcBridgeSessionDirectory = folder
        local source = box name:"UMB_PreviewSource"
        append created source
        setUserProp source "KCB_NodeId" "fixture-source"
        setUserProp source "KCB_Session" kcBridgeSessionId
        local outside = box name:"UMB_Preexisting"
        append created outside
        kcBridgeNodes = #(source)
        kcBridgeSceneBaseline = for node in objects collect getHandleByAnim node
        writeRegistry registry
        local plan = kcBridgePreviewPlan()
        if not plan[1] or plan[3].count != 0 do throw "Original/pre-existing objects were reported as new."
        append results "PASS existing and pre-session objects are excluded"

        local standalone = box name:"UMB_Standalone"
        append created standalone
        local before = getSaveRequired()
        plan = kcBridgePreviewPlan()
        if plan[3].count != 1 or plan[3][1][2] != "" do throw "Standalone mesh was not planned as a new FBX."
        if (getUserProp standalone "KCB_NodeId") != undefined or before != getSaveRequired() do throw "Preview changed scene state."
        if plan[2] != "Assets/Preview Models" do throw "Wrong output directory."
        append results "PASS standalone mesh creates a new FBX; preview does not tag or dirty nodes"

        local linked = box name:"UMB_Linked"
        append created linked
        linked.parent = source
        plan = kcBridgePreviewPlan()
        local linkedRows = for row in plan[3] where row[1] == linked collect row
        if linkedRows.count != 1 or linkedRows[1][2] != "Assets/Preview Models/Original.fbx" do throw "Linked mesh did not target its source FBX."
        append results "PASS linked addition targets its existing source FBX"
        kcBridgeRefreshPreview()
        local visible = kcBridgeRollout.updatePlan.text
        if (findString visible "NEW FBX in Assets/Preview Models") == undefined or (findString visible "ADD TO Assets/Preview Models/Original.fbx") == undefined do throw "Visible preview does not show the file actions."
        if not kcBridgeRollout.updatePlan.readOnly do throw "Preview details are editable."
        append results "PASS visible read-only preview shows new-file and source-file actions"

        local duplicate = box name:"UMB_DuplicateId"
        append created duplicate
        setUserProp duplicate "KCB_NodeId" "fixture-source"
        setUserProp duplicate "KCB_Session" kcBridgeSessionId
        plan = kcBridgePreviewPlan()
        local duplicateRows = for row in plan[3] where row[1] == duplicate collect row
        if duplicateRows.count != 1 or duplicateRows[1][2] != "" do throw "Duplicate source ID hid a new mesh."
        append results "PASS duplicated source IDs are predicted without rewriting them"

        local child = box name:"UMB_NewChild"
        append created child
        child.parent = standalone
        plan = kcBridgePreviewPlan()
        local childRows = for row in plan[3] where row[1] == child collect row
        if childRows.count != 1 or childRows[1][2] != "" do throw "New parent incorrectly selected an existing source."
        append results "PASS child of an unregistered new mesh also gets a separate FBX"

        setUserProp standalone "KCB_NodeId" "fixture-applied"
        setUserProp standalone "KCB_Session" kcBridgeSessionId
        writeRegistry registry applied:true
        plan = kcBridgePreviewPlan()
        if (for row in plan[3] where row[1] == standalone collect row).count != 0 do throw "Applied mesh still appears as a new FBX."
        childRows = for row in plan[3] where row[1] == child collect row
        if childRows[1][2] != "Assets/Preview Models/Added.fbx" do throw "New child did not use the newly registered source."
        append results "PASS Unity registry refresh clears applied additions and updates child targets"

        writeRegistry registry complete:false
        if (kcBridgePreviewPlan())[1] do throw "Incomplete registry was trusted."
        append results "PASS incomplete metadata produces an unavailable preview"
    ) catch (failure = getCurrentException() as string)
    for node in created where isValidNode node do delete node
    kcBridgeSessionDirectory = previous[1]
    kcBridgeSessionId = previous[2]
    kcBridgeNodes = previous[3]
    kcBridgeSceneBaseline = previous[4]
    select (for node in selected where isValidNode node collect node)
    setSaveRequired dirty
    deleteFile registry
    removeDir folder
    kcBridgeRefreshPreview()
    if failure != undefined do append results ("FAIL " + failure)
    local report = createFile reportPath encoding:#utf8
    for line in results do format "%\n" line to:report
    close report
    if failure != undefined do throw failure
    results
)
