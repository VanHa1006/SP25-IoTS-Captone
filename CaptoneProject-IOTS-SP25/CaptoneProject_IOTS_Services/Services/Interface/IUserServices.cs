﻿using CaptoneProject_IOTS_BOs;
using CaptoneProject_IOTS_BOs.DTO.PaginationDTO;
using CaptoneProject_IOTS_BOs.DTO.UserDTO;
using CaptoneProject_IOTS_BOs.Models;
using CaptoneProject_IOTS_Service.Business;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CaptoneProject_IOTS_Service.Services.Interface
{
    public interface IUserServices
    {
        Task<ResponseDTO> LoginUserAsync(string email, string password);
        Task<ResponseDTO> GetUsersPagination(PaginationRequest paginationRequest, int? roleId);
        Task<ResponseDTO> GetAllUsers();
        Task<ResponseDTO> UpdateUserRole(int userId, List<int>? roleList);
        Task<ResponseDTO> UpdateUserStatus(int userId, int isActive);
        Task<GenericResponseDTO<UserDetailsResponseDTO>> GetUserDetailsById(int id);
        //Task<GenericResponseDTO<UserDetailsResponseDTO>> CreateOrUpdateUser(int id, UserCreateOrUpdateRequestDTO payload);
        Task<ResponseDTO> CreateStaffOrManager(UserCreateOrUpdateRequestDTO payload);
        Task<ResponseDTO> StaffManagerVerifyOTP(string otp, int requestId, int requestStatusId, string password);
        Task<GenericResponseDTO<User>> GetLoginUser();
    }
}
