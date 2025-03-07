﻿using CaptoneProject_IOTS_BOs.DTO.UserDTO;
using CaptoneProject_IOTS_BOs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CaptoneProject_IOTS_Service.Services.Interface
{
    public interface IStaffManagerService
    {
        Task<ResponseDTO> CreateStaffOrManager(CreateUserDTO payload);
        Task<ResponseDTO> StaffManagerVerifyOTP(
            string otp,
            int requestId,
            int requestStatusId,
            string password
        );

        //Task<ResponseDTO> CreateStaffManagerRequestVerifyOtp(string email);

    }
}
