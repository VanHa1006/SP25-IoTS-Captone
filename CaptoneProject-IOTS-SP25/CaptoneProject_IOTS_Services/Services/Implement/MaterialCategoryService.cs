﻿using CaptoneProject_IOTS_BOs;
using CaptoneProject_IOTS_BOs.Constant;
using CaptoneProject_IOTS_BOs.DTO.MaterialCategotyDTO;
using CaptoneProject_IOTS_BOs.DTO.MaterialDTO;
using CaptoneProject_IOTS_BOs.DTO.PaginationDTO;
using CaptoneProject_IOTS_BOs.Models;
using CaptoneProject_IOTS_Repository.Repository.Implement;
using CaptoneProject_IOTS_Service.Business;
using CaptoneProject_IOTS_Service.ResponseService;
using CaptoneProject_IOTS_Service.Services.Interface;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static CaptoneProject_IOTS_BOs.Constant.UserEnumConstant;

namespace CaptoneProject_IOTS_Service.Services.Implement
{
    public class MaterialCategoryService : IMaterialCategoryService
    {
        private readonly UnitOfWork _unitOfWork;
        private readonly IFileService fileService;
        private readonly IUserServices userServices;

        public MaterialCategoryService(
            IFileService fileService,
            IUserServices userServices
        )
        {
            _unitOfWork ??= new UnitOfWork();
            this.fileService = fileService;
            this.userServices = userServices;
        }

        public async Task<ResponseDTO> CreateOrUpdateMaterialCategory(int? id, CreateUpdateMaterialCategoryDTO payload)
        {
            MaterialCategory materialCategory = (id == null) ? new MaterialCategory() : _unitOfWork.MaterialCategoryRepository.GetById((int)id);

            var loginUser = userServices.GetLoginUser();

            if (loginUser == null || !await userServices.CheckLoginUserRole(RoleEnum.ADMIN) || !await userServices.CheckLoginUserRole(RoleEnum.MANAGER))
                return ResponseService<IotDeviceDetailsDTO>.Unauthorize("You don't have permission to access");

            var loginUserId = loginUser.Id;


            if (materialCategory == null)
                return ResponseService<Object>.NotFound(ExceptionMessage.MATERIAL_CATEGORY_NOTFOUND);

            try
            {
                materialCategory.Label = payload.Label;
                materialCategory.IsActive = 1;
                materialCategory.CreatedDate = DateTime.Now; ;
                materialCategory.CreatedBy = loginUserId;
                

                MaterialCategory? response;

                if (id > 0) //Update
                    response = _unitOfWork.MaterialCategoryRepository.Update(materialCategory);
                else //Create
                    response = _unitOfWork.MaterialCategoryRepository.Create(materialCategory);

                return await GetByMaterialCategoryId(response.Id);
            }
            catch (Exception ex)
            {
                return ResponseService<Object>.BadRequest(ex.Message);
            }
        }

        public async Task<ResponseDTO> CreateOrUpdateMaterialCategoryToStore(int? id, CreateUpdateMaterialCategoryDTO payload)
        {
            MaterialCategory materialCategory = (id == null) ? new MaterialCategory() : _unitOfWork.MaterialCategoryRepository.GetById((int)id);

            var loginUser = userServices.GetLoginUser();

            if (loginUser == null || !await userServices.CheckLoginUserRole(RoleEnum.STORE))
                return ResponseService<IotDeviceDetailsDTO>.Unauthorize("You don't have permission to access");

            var loginUserId = loginUser.Id;


            if (materialCategory == null)
                return ResponseService<Object>.NotFound(ExceptionMessage.MATERIAL_CATEGORY_NOTFOUND);

            try
            {
                materialCategory.Label = payload.Label;
                materialCategory.IsActive = 2;
                materialCategory.CreatedDate = DateTime.Now; 
                materialCategory.CreatedBy = loginUserId;

                MaterialCategory? response;

                if (id > 0) //Update
                    response = _unitOfWork.MaterialCategoryRepository.Update(materialCategory);
                else //Create
                    response = _unitOfWork.MaterialCategoryRepository.Create(materialCategory);

                return await GetByMaterialCategoryId(response.Id);
            }
            catch (Exception ex)
            {
                return ResponseService<Object>.BadRequest(ex.Message);
            }
        }

        public Task<IBusinessResult> DeleteMaterialCategoryAsync(int id)
        {
            throw new NotImplementedException();
        }

        public async Task<IBusinessResult> GetAllMaterialCategory()
        {
            return new BusinessResult(1, "Get all category success", await _unitOfWork.MaterialCategoryRepository.GetAllMaterialCaterial());
        }

        public async Task<ResponseDTO> GetAllMaterialCategory(string searchKeyword)
        {
            PaginationResponseDTO<MaterialCategory> res = _unitOfWork.MaterialCategoryRepository.GetPaginate(
                filter: m => m.Label.Contains(searchKeyword) && m.IsActive > 0,
                orderBy: null,
                includeProperties: "",
                pageIndex: 0,
                pageSize: 500000
            );

            return new ResponseDTO
            {
                IsSuccess = true,
                Message = "Success",
                StatusCode = System.Net.HttpStatusCode.OK,
                Data = res
            };
        }

        public async Task<GenericResponseDTO<MaterialCategory>> GetByMaterialCategoryId(int id)
        {
            var res = _unitOfWork.MaterialCategoryRepository.GetById(id);

            if (res == null)
                return ResponseService<MaterialCategory>.NotFound("Not Found");

            return ResponseService<MaterialCategory>.OK(res);
        }

        public async Task<GenericResponseDTO<PaginationResponseDTO<MaterialCategory>>> GetPaginationMaterialCategories(PaginationRequest paginate, int? statusFilter)
        {
            PaginationResponseDTO<MaterialCategory> res = _unitOfWork.MaterialCategoryRepository.GetPaginate(
                filter: m => m.Label.Contains(paginate.SearchKeyword)
                    && (statusFilter == null || m.IsActive == statusFilter),
                orderBy: orderBy => orderBy.OrderByDescending(item => item.Id),
                includeProperties: "",
                pageIndex: paginate.PageIndex,
                pageSize: paginate.PageSize
            );

            return ResponseService<PaginationResponseDTO<MaterialCategory>>.OK(res);
        }

        public async Task<ResponseDTO> UpdateMaterialCategoryStatus(int id, int isActive)
        {
            MaterialCategory res = _unitOfWork.MaterialCategoryRepository.GetById(id);

            if (res == null)
                return new ResponseDTO
                {
                    IsSuccess = false,
                    StatusCode = System.Net.HttpStatusCode.NotFound,
                    Message = ExceptionMessage.MATERIAL_CATEGORY_NOTFOUND
                };

            res.IsActive = isActive;

            var response = _unitOfWork.MaterialCategoryRepository.Update(res);

            return await GetByMaterialCategoryId(response.Id);
        }
    }
}
