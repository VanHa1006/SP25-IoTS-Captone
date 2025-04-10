﻿using CaptoneProject_IOTS_BOs;
using CaptoneProject_IOTS_BOs.DTO.NotificationDTO;
using CaptoneProject_IOTS_BOs.DTO.PaginationDTO;
using CaptoneProject_IOTS_BOs.DTO.StoreDTO;
using CaptoneProject_IOTS_BOs.DTO.UserDTO;
using CaptoneProject_IOTS_BOs.DTO.UserRequestDTO;
using CaptoneProject_IOTS_Service.Business;
using CaptoneProject_IOTS_Service.Services.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OData;
using System.Net;
using System.Reflection;
using static CaptoneProject_IOTS_BOs.Constant.EntityTypeConst;
using static CaptoneProject_IOTS_BOs.Constant.UserEnumConstant;
using static CaptoneProject_IOTS_BOs.Constant.UserRequestConstant;
using static CaptoneProject_IOTS_BOs.DTO.StoreDTO.StoreDTO;

namespace CaptoneProject_IOTS_API.Controllers.UserController
{
    [Route("api/store")]
    [ApiController]
    public class StoreController : ControllerBase
    {
        private readonly IStoreService _storeService;
        private readonly IUserRequestService _userRequestService;
        private readonly IActivityLogService activityLogService;
        private readonly INotificationService notificationService;
        private IActionResult GetActionResult(ResponseDTO response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Unauthorized(response);
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return NotFound(response);
            }
            else if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return BadRequest(response);
            }

            return Ok(response);
        }
        public StoreController(IStoreService _storeService,
            IUserRequestService userRequestService,
            IActivityLogService activityLogService,
            INotificationService notificationService)
        {
            this._storeService = _storeService;
            this._userRequestService = userRequestService;
            this.activityLogService = activityLogService;
            this.notificationService = notificationService;
        }

        [HttpPost("register-store-user")]
        public async Task<IActionResult> RegisterStoreUser(
            [FromBody] UserRegisterDTO payload 
        )
        {
            var response = await _storeService.RegisterStoreUser(payload);

            return GetActionResult(response);
        }

        [HttpPost("create-store-user-request-verify-otp")]
        public async Task<IActionResult> CreateStoreUserRequest([FromBody] CreateUserRequestDTO request)
        {
            var response = await _storeService.CreateStoreUserRequestVerifyOtp(request.Email);

            return GetActionResult(response);
        }

        [HttpPost("submit-pending-to-approve-store-request/{requestId}")]
        [Authorize]
        public async Task<IActionResult> SubmitPendingToApproveUserRequest(int requestId)
        {
            var res = await _userRequestService.UpdateUserRequestStatus(requestId, UserRequestStatusEnum.PENDING_TO_APPROVE);

            if (res.IsSuccess)
            {
                var email = res?.Data?.userDetails?.Email;

                _ = notificationService.CreateStaffManagerAdminNotification(
                    new NotificationRequestDTO
                    {
                        Title = "{UserEmail} submit a store request account for you to approve".Replace("{UserEmail}", email),
                        Content = "{UserEmail} submit a store request account for you to approve".Replace("{UserEmail}", email),
                        EntityId = requestId,
                        EntityType = (int)EntityTypeEnum.USER_REQUEST
                    },
                    RoleEnum.STAFF
                ).ConfigureAwait(false);
            }

            return GetActionResult(res);
        }

        [HttpPost("create-or-update-store/{userId}")]
        public async Task<IActionResult> CreateOrUpdateStoreByUserId(int userId,
            [FromBody] StoreRequestDTO payload)
        {
            var response = await _storeService.CreateOrUpdateStoreByLoginUser(userId, payload);

            //if (response.IsSuccess)
            //{
            //    activityLogService.CreateUserHistoryTrackingActivityLog("Update Store", response.Data.Name, "Update");
            //}

            return GetActionResult(response);
        }

        [HttpPost("create-or-update-business-license")]
        public async Task<IActionResult> CreateOrUpdateBusinessLicenses(
            [FromBody] StoreBusinessLicensesDTO payload)
        {
            var res = await _storeService.CreateOrUpdateBusinessLicences(payload);

            return GetActionResult(res);
        }

        [HttpGet("get-business-license/{storeId}")]
        public async Task<IActionResult> GetBusinessLicenseByStoreId(int storeId)
        {
            var res = await _storeService.GetBusinessLicencesByStoreId(storeId);

            return GetActionResult(res);
        }

        [HttpGet("get-store-details-by-user-id/{userId}")]
        public async Task<IActionResult> GetStoreDetailsByUserId(int userId)
        {
            var response = await _storeService.GetStoreDetailsByUserId(userId);

            return GetActionResult(response);
        }

        [HttpGet("get-store-details-by-store-id/{storeId}")]
        public async Task<IActionResult> GetStoreDetailsByStoreId(int storeId)
        {
            var response = await _storeService.GetStoreDetailsByStoreId(storeId);

            return GetActionResult(response);
        }

        [HttpPost("get-pagination-stores")]
        public async Task<IActionResult> GetPaginationStores([FromBody] PaginationRequest paginationRequest)
        {
            var response = await _storeService.GetPaginationStores(paginationRequest);

            return GetActionResult(response);
        }

        [HttpPost("get-pagination-product-by-stores-id")]
        public async Task<IActionResult> GetPaginationStoresDetails([FromBody] PaginationRequest paginationRequest, int storeId)
        {
            var response = await _storeService.GetPaginationStoresDetails(paginationRequest, storeId);

            return GetActionResult(response);
        }
    }
}
