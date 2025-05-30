﻿using CaptoneProject_IOTS_BOs.Models;
using CaptoneProject_IOTS_Repository.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CaptoneProject_IOTS_Repository.Repository.Implement
{
    public class TrainerBusinessLicensesRepository : RepositoryBase<TrainerBusinessLicense>
    {
        public TrainerBusinessLicense? GetByTrainerId(int trainerId)
        {
            return _dbSet.SingleOrDefault(item => item.TrainerId == trainerId);
        }
    }
}
