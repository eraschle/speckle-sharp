﻿using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Objects.BuiltElements;
using Objects.BuiltElements.Revit;
using Objects.BuiltElements.Revit;
using Objects.Geometry;
using Speckle.Core.Models;
using DB = Autodesk.Revit.DB;
using Opening = Objects.BuiltElements.Opening;

namespace Objects.Converter.Revit
{
  public partial class ConverterRevit
  {
    public List<ApplicationPlaceholderObject> FloorToNative(BuiltElements.Floor speckleFloor)
    {
      if (speckleFloor.outline == null)
      {
        throw new Speckle.Core.Logging.SpeckleException("Only outline based Floor are currently supported.");
      }

      bool structural = false;
      var outline = CurveToNative(speckleFloor.outline);

      DB.Level level;

      if (speckleFloor is RevitFloor speckleRevitFloor)
      {
        level = LevelToNative(speckleRevitFloor.level);
        structural = speckleRevitFloor.structural;
      }
      else
      {
        level = LevelToNative(LevelFromCurve(outline.get_Item(0)));
      }

      var floorType = GetElementType<FloorType>(speckleFloor);

      // NOTE: I have not found a way to edit a slab outline properly, so whenever we bake, we renew the element. The closest thing would be:
      // https://adndevblog.typepad.com/aec/2013/10/change-the-boundary-of-floorsslabs.html
      // This would only work if the floors have the same number (and type!!!) of outline curves. 
      var docObj = GetExistingElementByApplicationId(speckleFloor.applicationId);
      if (docObj != null)
      {
        Doc.Delete(docObj.Id);
      }

      DB.Floor revitFloor;
      if (floorType == null)
      {
        revitFloor = Doc.Create.NewFloor(outline, structural);
      }
      else
      {
        revitFloor = Doc.Create.NewFloor(outline, floorType, level, structural);
      }

      Doc.Regenerate();

      try
      {
        CreateVoids(revitFloor, speckleFloor);
      }
      catch (Exception ex)
      {
        ConversionErrors.Add(new Exception($"Could not create openings in floor {speckleFloor.applicationId}", ex));
      }

      SetInstanceParameters(revitFloor, speckleFloor);

      var placeholders = new List<ApplicationPlaceholderObject>() { new ApplicationPlaceholderObject { applicationId = speckleFloor.applicationId, ApplicationGeneratedId = revitFloor.UniqueId, NativeObject = revitFloor } };

      var hostedElements = SetHostedElements(speckleFloor, revitFloor);
      placeholders.AddRange(hostedElements);

      return placeholders;
    }

    private RevitFloor FloorToSpeckle(DB.Floor revitFloor)
    {
      var profiles = GetProfiles(revitFloor);

      var speckleFloor = new RevitFloor();
      speckleFloor.type = Doc.GetElement(revitFloor.GetTypeId()).Name;
      speckleFloor.outline = profiles[0];
      if (profiles.Count > 1)
      {
        speckleFloor.voids = profiles.Skip(1).ToList();
      }

      speckleFloor.level = ConvertAndCacheLevel(revitFloor, BuiltInParameter.LEVEL_PARAM);
      speckleFloor.structural = GetParamValue<bool>(revitFloor, BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);

      GetAllRevitParamsAndIds(speckleFloor, revitFloor, new List<string> { "LEVEL_PARAM", "FLOOR_PARAM_IS_STRUCTURAL" });

      speckleFloor.displayMesh = GetElementDisplayMesh(revitFloor, new Options() { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false });

      GetHostedElements(speckleFloor, revitFloor);

      return speckleFloor;
    }

    //Nesting the various profiles into a polycurve segments
    private List<ICurve> GetProfiles(DB.CeilingAndFloor floor)
    {
      var profiles = new List<ICurve>();
      var faces = HostObjectUtils.GetTopFaces(floor);
      Face face = floor.GetGeometryObjectFromReference(faces[0]) as Face;
      var crvLoops = face.GetEdgesAsCurveLoops();
      foreach (var crvloop in crvLoops)
      {
        var poly = new Polycurve(ModelUnits);
        foreach (var curve in crvloop)
        {
          var c = curve;

          if (c == null)
          {
            continue;
          }

          poly.segments.Add(CurveToSpeckle(c));
        }
        profiles.Add(poly);
      }
      return profiles;
    }
  }
}
