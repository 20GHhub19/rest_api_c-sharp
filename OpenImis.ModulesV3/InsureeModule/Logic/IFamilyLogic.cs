﻿using OpenImis.ModulesV3.InsureeModule.Models;
using OpenImis.ModulesV3.InsureeModule.Models.EnrollFamilyModels;
using System;

namespace OpenImis.ModulesV3.InsureeModule.Logic
{
    public interface IFamilyLogic
    {
        FamilyModel GetByCHFID(string chfid, Guid userUUID);
        NewFamilyResponse Create(EnrollFamilyModel model, int userId, int officerId);
        int GetUserIdByUUID(Guid uuid);
        int GetOfficerIdByUserUUID(Guid userUUID);
    }
}