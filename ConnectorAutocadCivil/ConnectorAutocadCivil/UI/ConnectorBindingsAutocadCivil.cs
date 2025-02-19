﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Collections;

using Speckle.Newtonsoft.Json;
using Speckle.Core.Models;
using Speckle.Core.Kits;
using Speckle.Core.Api;
using Speckle.DesktopUI;
using Speckle.DesktopUI.Utils;
using Speckle.Core.Transports;
using Speckle.ConnectorAutocadCivil.Entry;
using Speckle.ConnectorAutocadCivil.Storage;

using AcadApp = Autodesk.AutoCAD.ApplicationServices;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;

using Stylet;
using Autodesk.AutoCAD.DatabaseServices;

namespace Speckle.ConnectorAutocadCivil.UI
{
  public partial class ConnectorBindingsAutocad : ConnectorBindings
  {
    public Document Doc => Application.DocumentManager.MdiActiveDocument;

    /// <summary>
    /// TODO: Any errors thrown should be stored here and passed to the ui state
    /// </summary>
    public List<Exception> Exceptions { get; set; } = new List<Exception>();

    public ConnectorBindingsAutocad() : base()
    {
    }

    public void SetExecutorAndInit()
    {
      Application.DocumentManager.DocumentActivated += Application_DocumentActivated;
      Doc.BeginDocumentClose += Application_DocumentClosed;
    }

    #region local streams 

    public override void AddNewStream(StreamState state)
    {
      SpeckleStreamManager.AddSpeckleStream(state.Stream.id, JsonConvert.SerializeObject(state));
    }

    public override void RemoveStreamFromFile(string streamId)
    {
      SpeckleStreamManager.RemoveSpeckleStream(streamId);
    }

    public override void PersistAndUpdateStreamInFile(StreamState state)
    {
      SpeckleStreamManager.UpdateSpeckleStream(state.Stream.id, JsonConvert.SerializeObject(state));
    }

    public override List<StreamState> GetStreamsInFile()
    {
      List<string> strings = SpeckleStreamManager.GetSpeckleStreams();
      return strings.Select(s => JsonConvert.DeserializeObject<StreamState>(s)).ToList();
    }
    #endregion

    #region boilerplate

    public override string GetActiveViewName()
    {
      return "Entire Document"; // TODO: handle views
    }

    public override List<string> GetObjectsInView() // TODO: this returns all visible doc objects. handle views later.
    {
      var objs = new List<string>();
      using (AcadDb.Transaction tr = Doc.Database.TransactionManager.StartTransaction())
      {
        AcadDb.BlockTable blckTbl = tr.GetObject(Doc.Database.BlockTableId, AcadDb.OpenMode.ForRead) as AcadDb.BlockTable;
        AcadDb.BlockTableRecord blckTblRcrd = tr.GetObject(blckTbl[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead) as AcadDb.BlockTableRecord;
        foreach (AcadDb.ObjectId id in blckTblRcrd)
        {
          var dbObj = tr.GetObject(id, AcadDb.OpenMode.ForRead);
          if (dbObj.Visible())
            objs.Add(dbObj.Handle.ToString());
        }
        tr.Commit();
      }
      return objs;
    }

    public override string GetHostAppName() => Utils.AutocadAppName;

    public override string GetDocumentId()
    {
      string path = AcadDb.HostApplicationServices.Current.FindFile(Doc.Name, Doc.Database, AcadDb.FindFileHint.Default);
      return Speckle.Core.Models.Utilities.hashString("X" + path + Doc?.Name, Speckle.Core.Models.Utilities.HashingFuctions.MD5); // what is the "X" prefix for?
    }

    public override string GetDocumentLocation() => AcadDb.HostApplicationServices.Current.FindFile(Doc.Name, Doc.Database, AcadDb.FindFileHint.Default);

    public override string GetFileName() => Doc?.Name;

    public override List<string> GetSelectedObjects()
    {
      var objs = new List<string>();
      PromptSelectionResult selection = Doc.Editor.SelectImplied();
      if (selection.Status == PromptStatus.OK)
        objs = selection.Value.GetHandles();
      return objs;
    }

    public override List<ISelectionFilter> GetSelectionFilters()
    {
      var layers = new List<string>();
      using (AcadDb.Transaction tr = Doc.Database.TransactionManager.StartTransaction())
      {
        AcadDb.LayerTable lyrTbl = tr.GetObject(Doc.Database.LayerTableId, AcadDb.OpenMode.ForRead) as AcadDb.LayerTable;
        foreach (AcadDb.ObjectId objId in lyrTbl)
        {
          AcadDb.LayerTableRecord lyrTblRec = tr.GetObject(objId, AcadDb.OpenMode.ForRead) as AcadDb.LayerTableRecord;
          layers.Add(lyrTblRec.Name);
        }
        tr.Commit();
      }
      return new List<ISelectionFilter>()
      {
         new ListSelectionFilter {Slug="layer",  Name = "Layers", Icon = "LayersTriple", Description = "Selects objects based on their layers.", Values = layers },
         new AllSelectionFilter {Slug="all",  Name = "All", Icon = "CubeScan", Description = "Selects all document objects." }
      };
    }

    public override void SelectClientObjects(string args)
    {
      throw new NotImplementedException();
    }

    private void UpdateProgress(ConcurrentDictionary<string, int> dict, ProgressReport progress)
    {
      if (progress == null)
      {
        return;
      }

      Execute.PostToUIThread(() =>
      {
        progress.ProgressDict = dict;
        progress.Value = dict.Values.Last();
      });
    }

    #endregion

    #region receiving 

    public override async Task<StreamState> ReceiveStream(StreamState state)
    {
      Exceptions.Clear();

      var kit = KitManager.GetDefaultKit();
      var converter = kit.LoadConverter(Utils.AutocadAppName);
      var transport = new ServerTransport(state.Client.Account, state.Stream.id);

      var stream = await state.Client.StreamGet(state.Stream.id);

      if (state.CancellationTokenSource.Token.IsCancellationRequested)
      {
        return null;
      }

      string referencedObject = state.Commit.referencedObject;
      string id = state.Commit.id;

      //if "latest", always make sure we get the latest commit when the user clicks "receive"
      if (id == "latest")
      {
        var res = await state.Client.BranchGet(state.CancellationTokenSource.Token, state.Stream.id, state.Branch.name, 1);
        referencedObject = res.commits.items.FirstOrDefault().referencedObject;
        id = res.id;
      }

      //var commit = state.Commit;

      var commitObject = await Operations.Receive(
        referencedObject,
        state.CancellationTokenSource.Token,
        transport,
        onProgressAction: d => UpdateProgress(d, state.Progress),
        onTotalChildrenCountKnown: num => Execute.PostToUIThread(() => state.Progress.Maximum = num),
        onErrorAction: (message, exception) => { Exceptions.Add(exception); },
        disposeTransports: true
        );

      if (Exceptions.Count != 0)
      {
        RaiseNotification($"Encountered error: {Exceptions.Last().Message}");
      }

      using (DocumentLock l = Doc.LockDocument())
      {
        using (AcadDb.Transaction tr = Doc.Database.TransactionManager.StartTransaction())
        {
          // set the context doc for conversion - this is set inside the transaction loop because the converter retrieves this transaction for all db editing when the context doc is set!
          converter.SetContextDocument(Doc);

          // keep track of conversion progress here
          var conversionProgressDict = new ConcurrentDictionary<string, int>();
          conversionProgressDict["Conversion"] = 0;
          Execute.PostToUIThread(() => state.Progress.Maximum = state.SelectedObjectIds.Count());
          Action updateProgressAction = () =>
          {
            conversionProgressDict["Conversion"]++;
            UpdateProgress(conversionProgressDict, state.Progress);
          };

          // keep track of any layer name changes for notification here
          bool changedLayerNames = false;

          // create a commit layer prefix: all nested layers will be concatenated with this
          var layerPrefix = DesktopUI.Utils.Formatting.CommitInfo(stream.name, state.Branch.name, id);

          // give converter a way to access the commit info
          Doc.UserData.Add("commit", layerPrefix);

          // delete existing commit layers
          try
          {
            DeleteLayersWithPrefix(layerPrefix, tr);
          }
          catch
          {
            RaiseNotification($"could not remove existing layers starting with {layerPrefix} before importing new geometry.");
            state.Errors.Add(new Exception($"could not remove existing layers starting with {layerPrefix} before importing new geometry."));
          }

          // flatten the commit object to retrieve children objs
          int count = 0;
          var commitObjs = FlattenCommitObject(commitObject, converter, layerPrefix, state, ref count);

          // open model space block table record for write
          BlockTableRecord btr = (BlockTableRecord)tr.GetObject(Doc.Database.CurrentSpaceId, OpenMode.ForWrite);

          // TODO: create dictionaries here for linetype and layer linewidth
          // More efficient this way than doing this per object
          var lineTypeDictionary = new Dictionary<string, ObjectId>();
          var lineTypeTable = (LinetypeTable)tr.GetObject(Doc.Database.LinetypeTableId, OpenMode.ForRead);
          foreach (AcadDb.ObjectId lineTypeId in lineTypeTable)
          {
            var linetype = (LinetypeTableRecord)tr.GetObject(lineTypeId, OpenMode.ForRead);
            lineTypeDictionary.Add(linetype.Name, lineTypeId);
          }

          foreach (var commitObj in commitObjs)
          {
            // create the object's bake layer if it doesn't already exist
            (Base obj, string layerName) = commitObj;

            var converted = converter.ConvertToNative(obj);
            var convertedEntity = converted as Entity;

            if (convertedEntity != null)
            {
              if (GetOrMakeLayer(layerName, tr, out string cleanName))
              {
                // record if layer name has been modified
                if (!cleanName.Equals(layerName))
                  changedLayerNames = true;

                if (convertedEntity.Append(cleanName, tr, btr))
                {
                  // handle display
                  Base display = obj[@"displayStyle"] as Base;
                  if (display != null)
                  {
                    var color = display["color"] as int?;
                    var lineType = display["linetype"] as string;
                    var lineWidth = display["lineweight"] as double?;

                    if (color != null)
                    {
                      var systemColor = System.Drawing.Color.FromArgb((int)color);
                      convertedEntity.Color = Color.FromRgb(systemColor.R, systemColor.G, systemColor.B);
                      convertedEntity.Transparency = new Transparency(systemColor.A);
                    }
                    if (lineWidth != null)
                    {
                      convertedEntity.LineWeight = Utils.GetLineWeight((double)lineWidth);
                    }
                      
                    if (lineType != null)
                    {
                      if (lineTypeDictionary.ContainsKey(lineType))
                      {
                        convertedEntity.LinetypeId = lineTypeDictionary[lineType];
                      }
                    }
                  }
                  tr.TransactionManager.QueueForGraphicsFlush();
                }
                else
                {
                  state.Errors.Add(new Exception($"Failed to bake object {obj.id} of type {obj.speckle_type}."));
                }

              }
              else
                state.Errors.Add(new Exception($"Could not create layer {layerName} to bake objects into."));
            }
            else if (converted == null)
            {
              state.Errors.Add(new Exception($"Failed to convert object {obj.id} of type {obj.speckle_type}."));
            }
          }

          // raise any warnings from layer name modification
          if (changedLayerNames)
            state.Errors.Add(new Exception($"Layer names were modified: one or more layers contained invalid characters {Utils.invalidChars}"));

          // remove commit info from doc userdata
          Doc.UserData.Remove("commit");

          tr.Commit();
        }
      }

      return state;
    }

    // Recurses through the commit object and flattens it. Returns list of Base objects with their bake layers
    private List<Tuple<Base, string>> FlattenCommitObject(object obj, ISpeckleConverter converter, string layer, StreamState state, ref int count, bool foundConvertibleMember = false)
    {
      var objects = new List<Tuple<Base, string>>();

      if (obj is Base @base)
      {
        if (converter.CanConvertToNative(@base))
        {
          objects.Add(new Tuple<Base, string>(@base, layer));
          return objects;
        }
        else
        {
          int totalMembers = @base.GetDynamicMembers().Count();
          foreach (var prop in @base.GetDynamicMembers())
          {
            count++;

            // get bake layer name
            string objLayerName = prop.StartsWith("@") ? prop.Remove(0, 1) : prop;
            string acLayerName = $"{layer}${objLayerName}";

            var nestedObjects = FlattenCommitObject(@base[prop], converter, acLayerName, state, ref count, foundConvertibleMember);
            if (nestedObjects.Count > 0)
            {
              objects.AddRange(nestedObjects);
              foundConvertibleMember = true;
            }
          }
          if (!foundConvertibleMember && count == totalMembers ) // this was an unsupported geo
            state.Errors.Add(new Exception($"Receiving {@base.speckle_type} objects is not supported. Object {@base.id} not baked."));
          return objects;
        }
      }

      if (obj is List<object> list)
      {
        count = 0;
        foreach (var listObj in list)
          objects.AddRange(FlattenCommitObject(listObj, converter, layer, state, ref count )) ;
        return objects;
      }

      if (obj is IDictionary dict)
      {
        count = 0;
        foreach (DictionaryEntry kvp in dict)
          objects.AddRange(FlattenCommitObject(kvp.Value, converter, layer, state, ref count));
        return objects;
      }

      return objects;
    }

    private void DeleteLayersWithPrefix(string prefix, AcadDb.Transaction tr)
    {
      // Open the Layer table for read
      var lyrTbl = (AcadDb.LayerTable)tr.GetObject(Doc.Database.LayerTableId, AcadDb.OpenMode.ForRead);
      foreach (AcadDb.ObjectId layerId in lyrTbl)
      {
        AcadDb.LayerTableRecord layer = (AcadDb.LayerTableRecord)tr.GetObject(layerId, AcadDb.OpenMode.ForRead);
        string layerName = layer.Name;
        if (layerName.StartsWith(prefix))
        {
          layer.UpgradeOpen();

          // cannot delete current layer: swap current layer to default layer "0" if current layer is to be deleted
          if (Doc.Database.Clayer == layerId)
          {
            var defaultLayerID = lyrTbl["0"];
            Doc.Database.Clayer = defaultLayerID;
          }
          layer.IsLocked = false;

          // delete all objects on this layer
          // TODO: this is ugly! is there a better way to delete layer objs instead of looping through each one?
          var bt = (AcadDb.BlockTable)tr.GetObject(Doc.Database.BlockTableId, AcadDb.OpenMode.ForRead);
          foreach (var btId in bt)
          {
            var block = (AcadDb.BlockTableRecord)tr.GetObject(btId, AcadDb.OpenMode.ForRead);
            foreach (var entId in block)
            {
              var ent = (AcadDb.Entity)tr.GetObject(entId, AcadDb.OpenMode.ForRead);
              if (ent.Layer == layerName)
              {
                ent.UpgradeOpen();
                ent.Erase();
              }
            }
          }

          layer.Erase();
        }
      }
    }

    private bool GetOrMakeLayer(string layerName, AcadDb.Transaction tr, out string cleanName)
    {
      cleanName = Utils.RemoveInvalidChars(layerName);
      try
      {
        AcadDb.LayerTable lyrTbl = tr.GetObject(Doc.Database.LayerTableId, AcadDb.OpenMode.ForRead) as AcadDb.LayerTable;
        if (lyrTbl.Has(cleanName))
        {
          return true;
        }
        else
        {
          lyrTbl.UpgradeOpen();
          var _layer = new AcadDb.LayerTableRecord();

          // Assign the layer properties
          _layer.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByColor, 7); // white
          _layer.Name = cleanName;

          // Append the new layer to the layer table and the transaction
          lyrTbl.Add(_layer);
          tr.AddNewlyCreatedDBObject(_layer, true);
        }
      }
      catch { return false; }
      return true;
    }

    #endregion

    #region sending

    public override async Task<StreamState> SendStream(StreamState state)
    {
      var kit = KitManager.GetDefaultKit();
      var converter = kit.LoadConverter(Utils.AutocadAppName);
      converter.SetContextDocument(Doc);

      var streamId = state.Stream.id;
      var client = state.Client;

      if (state.Filter != null)
      {
        state.SelectedObjectIds = GetObjectsFromFilter(state.Filter, converter);
      }

      // remove deleted object ids
      var deletedElements = new List<string>();
      foreach (var handle in state.SelectedObjectIds)
        if (Doc.Database.TryGetObjectId(Utils.GetHandle(handle), out AcadDb.ObjectId id))
          if (id.IsErased || id.IsNull)
            deletedElements.Add(handle);
      state.SelectedObjectIds = state.SelectedObjectIds.Where(o => !deletedElements.Contains(o)).ToList();

      if (state.SelectedObjectIds.Count == 0)
      {
        RaiseNotification("Zero objects selected; send stopped. Please select some objects, or check that your filter can actually select something.");
        return state;
      }

      var commitObj = new Base();

      var units = Units.GetUnitsFromString(Doc.Database.Insunits.ToString());
      commitObj["units"] = units;

      var conversionProgressDict = new ConcurrentDictionary<string, int>();
      conversionProgressDict["Conversion"] = 0;
      Execute.PostToUIThread(() => state.Progress.Maximum = state.SelectedObjectIds.Count());
      int convertedCount = 0;
      bool renamedlayers = false;

      foreach (var autocadObjectHandle in state.SelectedObjectIds)
      {
        if (state.CancellationTokenSource.Token.IsCancellationRequested)
        {
          return null;
        }

        // get the db object from id
        AcadDb.Handle hn = Utils.GetHandle(autocadObjectHandle);
        AcadDb.DBObject obj = hn.GetObject(out string type, out string layer);

        if (obj == null)
        {
          state.Errors.Add(new Exception($"Failed to find local object ${autocadObjectHandle}."));
          continue;
        }

        if (!converter.CanConvertToSpeckle(obj))
        {
          state.Errors.Add(new Exception($"Objects of type ${type} are not supported"));
          continue;
        }

        // convert obj
        // try catch to prevent memory access violation crash in case a conversion goes wrong
        Base converted = null;
        string containerName = string.Empty;
        try
        {
          converted = converter.ConvertToSpeckle(obj);
          if (converted == null)
          {
            state.Errors.Add(new Exception($"Failed to convert object ${autocadObjectHandle} of type ${type}."));
            continue;
          }
        }
        catch
        {
          state.Errors.Add(new Exception($"Failed to convert object {autocadObjectHandle} of type {type}."));
          continue;
        }

        /* TODO: adding the extension dictionary / xdata per object 
        foreach (var key in obj.ExtensionDictionary)
        {
          converted[key] = obj.ExtensionDictionary.GetUserString(key);
        }
        */

        if (obj is BlockReference)
          containerName = "Blocks";
        else
        {
          // remove invalid chars from layer name
          string cleanLayerName = Utils.RemoveInvalidDynamicPropChars(layer);
          containerName = cleanLayerName;
          if (!cleanLayerName.Equals(layer))
            renamedlayers = true;
        }

        if (commitObj[$"@{containerName}"] == null)
          commitObj[$"@{containerName}"] = new List<Base>();
        ((List<Base>)commitObj[$"@{containerName}"]).Add(converted);

        conversionProgressDict["Conversion"]++;
        UpdateProgress(conversionProgressDict, state.Progress);

        converted.applicationId = autocadObjectHandle;

        convertedCount++;
      }

      if (renamedlayers)
        RaiseNotification("Replaced illegal chars ./ with - in one or more layer names.");

      if (state.CancellationTokenSource.Token.IsCancellationRequested)
      {
        return null;
      }

      Execute.PostToUIThread(() => state.Progress.Maximum = convertedCount);

      var transports = new List<ITransport>() { new ServerTransport(client.Account, streamId) };

      var commitObjId = await Operations.Send(
        commitObj,
        state.CancellationTokenSource.Token,
        transports,
        onProgressAction: dict => UpdateProgress(dict, state.Progress),
        onErrorAction: (err, exception) => { Exceptions.Add(exception); },
        disposeTransports: true
        );

      if (Exceptions.Count != 0)
      {
        RaiseNotification($"Failed to send: \n {Exceptions.Last().Message}");
        return null;
      }

      if (convertedCount > 0)
      {
        var actualCommit = new CommitCreateInput
        {
          streamId = streamId,
          objectId = commitObjId,
          branchName = state.Branch.name,
          message = state.CommitMessage != null ? state.CommitMessage : $"Pushed {convertedCount} elements from {Utils.AppName}.",
          sourceApplication = Utils.AutocadAppName
        };

        if (state.PreviousCommitId != null) { actualCommit.parents = new List<string>() { state.PreviousCommitId }; }

        try
        {
          var commitId = await client.CommitCreate(actualCommit);

          await state.RefreshStream();
          state.PreviousCommitId = commitId;

          PersistAndUpdateStreamInFile(state);
          RaiseNotification($"{convertedCount} objects sent to {state.Stream.name}.");
        }
        catch (Exception e)
        {
          Globals.Notify($"Failed to create commit.\n{e.Message}");
          state.Errors.Add(e);
        }
      }
      else
      {
        Globals.Notify($"Did not create commit: no objects could be converted.");
      }

      return state;
    }

    private List<string> GetObjectsFromFilter(ISelectionFilter filter, ISpeckleConverter converter)
    {
      switch (filter.Slug)
      {
        case "all":
          return Doc.ConvertibleObjects(converter);
        case "layer":
          var layerObjs = new List<string>();
          foreach (var layerName in filter.Selection)
          {
            AcadDb.TypedValue[] layerType = new AcadDb.TypedValue[1] { new AcadDb.TypedValue((int)AcadDb.DxfCode.LayerName, layerName) };
            PromptSelectionResult prompt = Doc.Editor.SelectAll(new SelectionFilter(layerType));
            if (prompt.Status == PromptStatus.OK)
              layerObjs.AddRange(prompt.Value.GetHandles());
          }
          return layerObjs;
        default:
          RaiseNotification("Filter type is not supported in this app. Why did the developer implement it in the first place?");
          return new List<string>();
      }
    }

    #endregion

    #region events

    private void Application_DocumentClosed(object sender, DocumentBeginCloseEventArgs e)
    {
      // Triggered just after a request is received to close a drawing.
      if (Doc != null)
        return;

      SpeckleAutocadCommand.Bootstrapper.Application.MainWindow.Hide();

      var appEvent = new ApplicationEvent() { Type = ApplicationEvent.EventType.DocumentClosed };
      NotifyUi(appEvent);
    }

    private void Application_DocumentActivated(object sender, DocumentCollectionEventArgs e)
    {
      // Triggered when a document window is activated. This will happen automatically if a document is newly created or opened.
      var appEvent = new ApplicationEvent()
      {
        Type = ApplicationEvent.EventType.DocumentOpened,
        DynamicInfo = GetStreamsInFile()
      };

      NotifyUi(appEvent);
    }
    #endregion
  }
}
