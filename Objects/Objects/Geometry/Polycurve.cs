﻿using Objects.Primitive;
using Speckle.Core.Kits;
using Speckle.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Objects.Geometry
{
  public class Polycurve : Base, ICurve, IHasArea, IHasBoundingBox
  {
    public List<ICurve> segments { get; set; } = new List<ICurve>();
    public Interval domain { get; set; }
    public bool closed { get; set; }
    public Box bbox { get; set; }
    public double area { get; set; }
    public double length { get; set; }

    public Polycurve()
    {
    }

    public Polycurve(string units = Units.Meters, string applicationId = null)
    {
      this.applicationId = applicationId;
      this.units = units;
    }

    public static implicit operator Polycurve(Polyline polyline)
    {
      Polycurve polycurve = new Polycurve
      {

        units = polyline.units,
        area = polyline.area,
        domain = polyline.domain,
        closed = polyline.closed,
        bbox = polyline.bbox,
        length = polyline.length
      };


      for (var i = 0; i < polyline.points.Count - 1; i++)
      {
        //close poly
        if (i == polyline.points.Count - 1 && polyline.closed)
        {
          var line = new Line(polyline.points[i], polyline.points[0], polyline.units);
          polycurve.segments.Add(line);
        }
        else
        {
          var line = new Line(polyline.points[i], polyline.points[i + 1], polyline.units);
          polycurve.segments.Add(line);
        }
      }

      return polycurve;
    }

    public List<double> ToList()
    {
      var list = new List<double>();
      list.Add(closed ? 1 : 0);
      list.Add(domain.start ?? 0);
      list.Add(domain.end ?? 1);

      var crvs = CurveArrayEncodingExtensions.ToArray(segments);
      list.Add(crvs.Count);
      list.AddRange(crvs);

      list.Add(Units.GetEncodingFromUnit(units));
      list.Insert(0, CurveTypeEncoding.PolyCurve);
      list.Insert(0, list.Count);

      return list;
    }

    public static Polycurve FromList(List<double> list)
    {
      var polycurve = new Polycurve();
      polycurve.closed = list[2] == 1;
      polycurve.domain = new Interval(list[3], list[4]);

      var temp = list.GetRange(6, (int)list[5]);
      polycurve.segments = CurveArrayEncodingExtensions.FromArray(temp);
      polycurve.units = Units.GetUnitFromEncoding(list[list.Count - 1]);
      return polycurve;
    }
  }
}
