// Build-time patcher for Workshop_Data/Managed/Assembly-CSharp.dll
//   publicize <in> <out>                       make the members WorkshopPlus uses public
//   redirect  <in-publicized> <plus.dll> <out> route Workshop's loading, list, publish, help and messages to TTVRPlus.dll
using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Patcher
{
    static readonly string[] Fields = { "defaultMaterial", "defaultTexture", "visibilityDropdown", "footerContents", "activeItem", "textureField", "objectItems", "worldItems", "activeItems", "objectTabActive", "message", "messageDescription", "listView", "tableView", "table", "detailView", "tableViewController", "titleField", "errorMessageText", "publishButton", "confirmationView", "objectLabel", "rttCamera", "rttImage", "objectRoot", "flipToggle", "thumbnailGenerator", "previewImage", "helpInstructionMessage", "descriptionField" };
    static readonly string[] Methods = { "LoadTexture", "AssignTexture", "SetErrorMessage", "ActivateDetailView", "AddOrUpdateMetadata", "LoadMetadata", "ActivateListView", "UpdateObjectPosition", "GetFilePath", "GenerateObjectData", "GenerateWorldData" };

    static int Main(string[] a)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(a[1])));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };
        if (a[0] == "publicize")
        {
            var asm = AssemblyDefinition.ReadAssembly(a[1], rp);
            var w = asm.MainModule.GetType("Workshop");
            foreach (var f in Fields) { var fd = w.Fields.Single(x => x.Name == f); fd.IsPrivate = false; fd.IsPublic = true; }
            foreach (var m in Methods) { var md = w.Methods.Single(x => x.Name == m); md.IsPrivate = false; md.IsPublic = true; }
            var sm = asm.MainModule.GetType("SteamManager");          // for Reconnect
            foreach (var f in new[] { "s_instance", "m_bInitialized", "s_EverInialized" }) { var fd = sm.Fields.Single(x => x.Name == f); fd.IsPrivate = false; fd.IsPublic = true; }
            asm.Write(a[2]);
            Console.WriteLine("publicized");
            return 0;
        }
        if (a[0] == "redirect")
        {
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(a[2])));
            var asm = AssemblyDefinition.ReadAssembly(a[1], rp);
            var plus = AssemblyDefinition.ReadAssembly(a[2], rp);
            var mod = asm.MainModule;
            var w = mod.GetType("Workshop");
            var wp = plus.MainModule.GetType("TTVRPlus.WorkshopPlus");

            // 1) Workshop.LoadObject(path, textureFile, out err) => WorkshopPlus.LoadObject(this, path, textureFile, out err)
            var load = w.Methods.Single(x => x.Name == "LoadObject");
            var src = wp.Methods.Single(x => x.Name == "LoadObject");
            var plusRef = new AssemblyNameReference(plus.Name.Name, plus.Name.Version);
            mod.AssemblyReferences.Add(plusRef);
            var wpRef = new TypeReference("TTVRPlus", "WorkshopPlus", mod, plusRef);
            var target = new MethodReference("LoadObject", load.ReturnType, wpRef) { HasThis = false };
            target.Parameters.Add(new ParameterDefinition(w));                                   // local Workshop type
            foreach (var p in load.Parameters) target.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
            var body = load.Body;
            body.Instructions.Clear(); body.ExceptionHandlers.Clear(); body.Variables.Clear();
            var il = body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            body.InitLocals = false;

            // 2) Workshop.Start() => WorkshopPlus.Start(this)   (shows the window first, loads items in parallel)
            var start = w.Methods.Single(x => x.Name == "Start");
            var startRef = new MethodReference("Start", mod.TypeSystem.Void, wpRef) { HasThis = false };
            startRef.Parameters.Add(new ParameterDefinition(w));
            start.Body.Instructions.Clear(); start.Body.ExceptionHandlers.Clear(); start.Body.Variables.Clear(); start.Body.InitLocals = false;
            var sp = start.Body.GetILProcessor();
            sp.Emit(OpCodes.Ldarg_0); sp.Emit(OpCodes.Call, startRef); sp.Emit(OpCodes.Ret);

            // 3) Workshop.LoadTexture(path) => WorkshopPlus.LoadTexture(this, path)   (adds DDS/TGA, no "?" placeholder)
            var lt = w.Methods.Single(x => x.Name == "LoadTexture");
            var ltRef = new MethodReference("LoadTexture", lt.ReturnType, wpRef) { HasThis = false };
            ltRef.Parameters.Add(new ParameterDefinition(w));
            ltRef.Parameters.Add(new ParameterDefinition("path", ParameterAttributes.None, lt.Parameters[0].ParameterType));
            lt.Body.Instructions.Clear(); lt.Body.ExceptionHandlers.Clear(); lt.Body.Variables.Clear(); lt.Body.InitLocals = false;
            var lp = lt.Body.GetILProcessor();
            lp.Emit(OpCodes.Ldarg_0); lp.Emit(OpCodes.Ldarg_1); lp.Emit(OpCodes.Call, ltRef); lp.Emit(OpCodes.Ret);

            // 4) Texture button: accept DDS/TGA too
            int fx = 0;
            foreach (var ins in w.Methods.Single(x => x.Name == "OnTextureBrowseButtonClick").Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Ldstr) continue;
                if ((string)ins.Operand == "JPG and PNG Files") { ins.Operand = "Image Files (PNG, JPG, DDS, TGA) "; fx++; }
                else if ((string)ins.Operand == "jpg") { ins.Operand = "jpg; *.jpeg; *.dds; *.tga"; fx++; }
            }
            if (fx != 2) throw new Exception("texture filter strings not found");

            // 5) item list: search/sort view, "Last updated" column
            var wl = new TypeReference("TTVRPlus", "WorkshopList", mod, plusRef);
            Redirect(mod, w.Methods.Single(x => x.Name == "ActivateListView"), wl, "ActivateListView", w);
            var ctl = mod.GetType("DynamicHeightTableViewController");
            Redirect(mod, ctl.Methods.Single(x => x.Name == "GetCellForRowInTableView"), wl, "GetCell", ctl);
            // publish: non-freezing coroutine (TTVRPlus.Publisher), and the vertex colour step without its O(n^2) array copies
            var pubRef = new TypeReference("TTVRPlus", "Publisher", mod, plusRef);
            Redirect(mod, w.Methods.Single(x => x.Name == "OnPublishButtonClicked"), pubRef, "OnPublishButtonClicked", w);
            var anc = mod.GetType("AverageNormalCalculator").Methods.Single(x => x.Name == "ComputeAverageNormals");
            var ancRef = new MethodReference("ComputeAverageNormals", anc.ReturnType, pubRef) { HasThis = false };
            foreach (var p in anc.Parameters) ancRef.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
            anc.Body.Instructions.Clear(); anc.Body.ExceptionHandlers.Clear(); anc.Body.Variables.Clear(); anc.Body.InitLocals = false;
            var ap = anc.Body.GetILProcessor();
            ap.Emit(OpCodes.Ldarg_0); ap.Emit(OpCodes.Ldarg_1); ap.Emit(OpCodes.Call, ancRef); ap.Emit(OpCodes.Ret);

            // 5b) New / Browse model dialogs: accept glTF and GLB too
            foreach (var mn in new[] { "OnNewButtonClicked", "OnObjFileBrowseButtonClick" })
            {
                int hits = 0;
                foreach (var ins in w.Methods.Single(x => x.Name == mn).Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Ldstr) continue;
                    if ((string)ins.Operand == "OBJ Files") { ins.Operand = "3D Models (OBJ, glTF, GLB) "; hits++; }
                    else if ((string)ins.Operand == "obj") { ins.Operand = "obj; *.gltf; *.glb"; hits++; }
                }
                if (hits != 2) throw new Exception("model filter strings not found in " + mn);
            }

            // 5c) multi-delete reuses the "are you sure" dialog
            var conf = w.Methods.Single(x => x.Name == "OnDeleteItemConfirmed");
            var confRef = new MethodReference("OnDeleteConfirmed", mod.TypeSystem.Boolean, wl) { HasThis = false };
            confRef.Parameters.Add(new ParameterDefinition(w));
            var cp = conf.Body.GetILProcessor(); var c0 = conf.Body.Instructions[0];
            cp.InsertBefore(c0, cp.Create(OpCodes.Ldarg_0)); cp.InsertBefore(c0, cp.Create(OpCodes.Call, confRef));
            cp.InsertBefore(c0, cp.Create(OpCodes.Brfalse, c0)); cp.InsertBefore(c0, cp.Create(OpCodes.Ret));
            var canc = w.Methods.Single(x => x.Name == "OnDeleteItemCancelled");
            var cancRef = new MethodReference("OnDeleteCancelled", mod.TypeSystem.Void, wl) { HasThis = false };
            cancRef.Parameters.Add(new ParameterDefinition(w));
            var kp = canc.Body.GetILProcessor(); var k0 = canc.Body.Instructions[0];
            kp.InsertBefore(k0, kp.Create(OpCodes.Ldarg_0)); kp.InsertBefore(k0, kp.Create(OpCodes.Call, cancRef));

            // 5d) Browse texture on an item that already had one: rebuild rather than swap the image
            var browseTex = w.Methods.Single(x => x.Name == "OnTextureBrowseButtonClick");
            var bat = new MethodReference("BrowseAssignTexture", mod.TypeSystem.Void, wpRef) { HasThis = false };
            bat.Parameters.Add(new ParameterDefinition(w));
            var assignDef = w.Methods.Single(x => x.Name == "AssignTexture");
            foreach (var p in assignDef.Parameters) bat.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
            int swaps = 0;
            foreach (var ins in browseTex.Body.Instructions)
                if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference && ((MethodReference)ins.Operand).Name == "AssignTexture")
                { ins.OpCode = OpCodes.Call; ins.Operand = bat; swaps++; }
            if (swaps != 1) throw new Exception("AssignTexture call not found in OnTextureBrowseButtonClick");

            // 5e) custom Workshop thumbnails: publishing asks ThumbPicker instead of the ThumbnailGenerator directly
            var god = w.Methods.Single(x => x.Name == "GenerateObjectData");
            var tg = mod.GetType("ThumbnailGenerator");
            var tpRef = new TypeReference("TTVRPlus", "ThumbPicker", mod, plusRef);
            var genDef = tg.Methods.Single(x => x.Name == "GenerateThumbnail");
            var gtRef = new MethodReference("GenerateThumbnail", genDef.ReturnType, tpRef) { HasThis = false };
            gtRef.Parameters.Add(new ParameterDefinition(tg));
            foreach (var p in genDef.Parameters) gtRef.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
            int thumbs = 0;
            foreach (var ins in god.Body.Instructions)
                if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference && ((MethodReference)ins.Operand).Name == "GenerateThumbnail")
                { ins.OpCode = OpCodes.Call; ins.Operand = gtRef; thumbs++; }
            if (thumbs != 1) throw new Exception("GenerateThumbnail call not found in GenerateObjectData");

            // 5f) mark (re)imports so texture size notes only show then
            var markRef = new MethodReference("MarkImport", mod.TypeSystem.Void, wpRef) { HasThis = false };
            foreach (var mn in new[] { "OnNewButtonClicked", "OnObjFileBrowseButtonClick", "OnTextureBrowseButtonClick", "OnTextureClearButtonClick" })
            {
                var md = w.Methods.Single(x => x.Name == mn);
                var mp = md.Body.GetILProcessor();
                mp.InsertBefore(md.Body.Instructions[0], mp.Create(OpCodes.Call, markRef));
            }

            // 6) error line colours (errors red, notes amber)
            Redirect(mod, w.Methods.Single(x => x.Name == "SetErrorMessage"), wpRef, "SetMessage", w);

            // 7) Help page: one scrolling page from TTVRPlus.Help
            var helpRef = new TypeReference("TTVRPlus", "Help", mod, plusRef);
            Redirect(mod, w.Methods.Single(x => x.Name == "UpdateHelpText"), helpRef, "UpdateHelpText", w);

            asm.Write(a[3]);
            Console.WriteLine("redirected LoadObject, help text updated");
            return 0;
        }
        Console.Error.WriteLine("usage"); return 1;
    }

    // replace an instance method's body with a call to a static method taking (this, args...)
    static void Redirect(ModuleDefinition mod, MethodDefinition m, TypeReference target, string name, TypeDefinition self)
    {
        var r = new MethodReference(name, m.ReturnType, target) { HasThis = false };
        r.Parameters.Add(new ParameterDefinition(self));
        foreach (var p in m.Parameters) r.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        m.Body.Instructions.Clear(); m.Body.ExceptionHandlers.Clear(); m.Body.Variables.Clear(); m.Body.InitLocals = false;
        var il = m.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        for (int i = 0; i < m.Parameters.Count; i++) il.Emit(OpCodes.Ldarg, m.Parameters[i]);
        il.Emit(OpCodes.Call, r); il.Emit(OpCodes.Ret);
    }
}
