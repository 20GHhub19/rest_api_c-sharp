﻿using System;
using System.Collections.Generic;
using System.Text;

namespace OpenImis.ModulesV3.InsureeModule.Models
{
    public class GetInsureeModel
    {
        public string CHFID { get; set; }
        public string PhotoPath { get; set; }
        public string InsureeName { get; set; }
        public DateTime DOB { get; set; }
        public string Gender { get; set; }
    }
}
