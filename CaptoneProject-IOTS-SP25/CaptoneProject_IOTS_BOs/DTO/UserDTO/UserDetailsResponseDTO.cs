﻿using CaptoneProject_IOTS_BOs.DTO.RoleDTO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using static CaptoneProject_IOTS_BOs.DTO.StoreDTO.StoreDTO;

namespace CaptoneProject_IOTS_BOs.DTO.UserDTO
{
    public class UpdateUserRoleRequestDTO
    {
        public List<int>? RoleIdList { get; set; }
    }
    public class UserResponseDTO
    {
        public int Id { get; set; }

        public string Fullname { set; get; }

        public string Username { get; set; }

        public string Email { get; set; }

        public string Phone { get; set; }

        public string Address { get; set; }

        public int ProvinceId { set; get; }

        public string? ProvinceName { set; get; }

        public int DistrictId { set; get; }

        public string? DistrictName { set; get; }

        public int WardId { set; get; }

        public string? WardName { set; get; }

        public int? Gender { set; get; }

        public int? CreatedBy { get; set; }

        public DateTime? CreatedDate { get; set; }

        
        public int? UpdatedBy { get; set; }

        public DateTime? UpdatedDate { get; set; }

        public int IsActive { get; set; }

        public List<RoleResponse>? Roles { set; get; }
        public string? ImageUrl { set; get; } = "";
    }

    public class UserDetailsResponseDTO
    {
        public int Id { get; set; }

        public string Fullname { set; get; }

        public string Username { get; set; }

        public string Email { get; set; }

        public string Phone { get; set; }

        public string Address { get; set; }
        public int? Gender { set; get; }

        public int? CreatedBy { get; set; }

        public DateTime? CreatedDate { get; set; }

        public int? UpdatedBy { get; set; }

        public DateTime? UpdatedDate { get; set; }
        public int IsActive { get; set; }

        public List<RoleResponse>? Roles { set; get; }
        public string? ImageUrl { set; get; } = "";
    }
}
