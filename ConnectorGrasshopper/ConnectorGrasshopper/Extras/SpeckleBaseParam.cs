﻿using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace ConnectorGrasshopper.Extras
{
    public class SpeckleBaseParam: GH_Param<GH_SpeckleBase>
    {
        public override Guid ComponentGuid => new Guid("55F13720-45C1-4B43-892A-25AE4D95EFF2");
        protected override Bitmap Icon => Properties.Resources.BaseParam;
        public override GH_Exposure Exposure => GH_Exposure.hidden;
        
        public SpeckleBaseParam(string name, string nickname, string description, GH_ParamAccess access): 
            this(name,nickname,description,ComponentCategories.PRIMARY_RIBBON, "Params", access){}
        
        
        public SpeckleBaseParam(string name, string nickname, string description, string category, string subcategory, GH_ParamAccess access) : base(name, nickname, description, category, subcategory, access)
        {
        }
        
        public SpeckleBaseParam() : this("Speckle Base", "SB","Base object for Speckle",GH_ParamAccess.item)
        {
            
        }
    }

}
