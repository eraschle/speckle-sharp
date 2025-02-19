﻿using Autodesk.Revit.DB;
using Objects.BuiltElements;
using Objects.Geometry;
using Speckle.Core.Models;
using System.Collections.Generic;
using System.Linq;
using DB = Autodesk.Revit.DB.Architecture;
using Point = Objects.Geometry.Point;

namespace Objects.Converter.Revit
{
  public partial class ConverterRevit
  {
    public List<ApplicationPlaceholderObject> RoomToNative(Room speckleRoom)
    {
      var revitRoom = GetExistingElementByApplicationId(speckleRoom.applicationId) as DB.Room;
      var level = LevelToNative(speckleRoom.level);


      //TODO: support updating rooms
      if (revitRoom != null)
      {
        Doc.Delete(revitRoom.Id);
      }

      revitRoom = Doc.Create.NewRoom(level, new UV(speckleRoom.center.x, speckleRoom.center.y));

      revitRoom.Name = speckleRoom.name;
      revitRoom.Number = speckleRoom.number;

      SetInstanceParameters(revitRoom, speckleRoom);

      var placeholders = new List<ApplicationPlaceholderObject>()
      {
        new ApplicationPlaceholderObject
        {
        applicationId = speckleRoom.applicationId,
        ApplicationGeneratedId = revitRoom.UniqueId,
        NativeObject = revitRoom
        }
      };

      return placeholders;

    }

    public BuiltElements.Room RoomToSpeckle(DB.Room revitRoom)
    {
      var profiles = GetProfiles(revitRoom);

      var speckleRoom = new Room();

      speckleRoom.name = revitRoom.get_Parameter(BuiltInParameter.ROOM_NAME).AsString();
      speckleRoom.number = revitRoom.Number;
      speckleRoom.center = (Point)LocationToSpeckle(revitRoom);
      speckleRoom.level = ConvertAndCacheLevel(revitRoom, BuiltInParameter.ROOM_LEVEL_ID);
      speckleRoom.outline = profiles[0];
      speckleRoom.area = GetParamValue<double>(revitRoom, BuiltInParameter.ROOM_AREA);
      if (profiles.Count > 1)
      {
        speckleRoom.voids = profiles.Skip(1).ToList();
      }

      GetAllRevitParamsAndIds(speckleRoom, revitRoom);
      speckleRoom.displayMesh = GetElementDisplayMesh(revitRoom);

      return speckleRoom;
    }




  }
}