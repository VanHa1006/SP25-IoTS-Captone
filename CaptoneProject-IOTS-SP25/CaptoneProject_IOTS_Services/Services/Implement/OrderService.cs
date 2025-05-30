﻿using CaptoneProject_IOTS_BOs;
using CaptoneProject_IOTS_BOs.Constant;
using CaptoneProject_IOTS_BOs.DTO.GHTKDTO;
using CaptoneProject_IOTS_BOs.DTO.OrderDTO;
using CaptoneProject_IOTS_BOs.DTO.OrderItemsDTO;
using CaptoneProject_IOTS_BOs.DTO.PaginationDTO;
using CaptoneProject_IOTS_BOs.DTO.RefundDTO;
using CaptoneProject_IOTS_BOs.DTO.VNPayDTO;
using CaptoneProject_IOTS_BOs.Models;
using CaptoneProject_IOTS_Service.ResponseService;
using CaptoneProject_IOTS_Service.Services.Interface;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Data;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using static CaptoneProject_IOTS_BOs.Constant.ProductConst;
using static CaptoneProject_IOTS_BOs.Constant.UserEnumConstant;

namespace CaptoneProject_IOTS_Service.Services.Implement
{
    public class OrderService : IOrderService
    {
        private readonly UnitOfWork _unitOfWork;
        private readonly IUserServices userServices;
        private readonly IVNPayService vnpayServices;
        private readonly IGHTKService _ghtkService;
        private readonly IEmailService _emailServices;
        private readonly int AUTO_COMPLETE_DAYS = 3;
        private readonly int MAX_CANCELLED_PER_DAYS = 5;
        private readonly IWalletService walletService;
        private readonly IActivityLogService activityLogService;
        private readonly int APPLICATION_FEE;
        public OrderService(IUserServices userServices,
            IVNPayService vnpayServices,
            IGHTKService ghtkService,
            IEmailService emailServices,
            IWalletService walletService,
            IActivityLogService activityLogService)
        {
            _unitOfWork ??= new UnitOfWork();
            this.userServices = userServices;
            this.vnpayServices = vnpayServices;
            this._ghtkService = ghtkService;
            this._emailServices = emailServices;
            this.walletService = walletService;
            this.activityLogService = activityLogService;

            var generalSetting = _unitOfWork?.GeneralSettingsRepository?.Search(item => true)?.FirstOrDefault();

            APPLICATION_FEE = generalSetting?.ApplicationFeePercent ?? 10;
            AUTO_COMPLETE_DAYS = generalSetting?.OrderSuccessDays ?? 3;
        }

        private string GetApplicationSerialNumberOrder(int userID)
        {
            string currentDateTime = DateTime.Now.ToString("yyyyMMddHHmmss");

            return "OD{UserId}{CurrentDateTime}"
                .Replace("{UserId}", userID.ToString())
                .Replace("{CurrentDateTime}", currentDateTime);
        }

        public async Task<GenericResponseDTO<OrderReturnPaymentDTO>> CheckOrderSuccessfull(int? id, VNPayRequestDTO dto)
        {
            var loginUser = userServices.GetLoginUser();
            if (loginUser == null || !await userServices.CheckLoginUserRole(RoleEnum.CUSTOMER))
                return ResponseService<OrderReturnPaymentDTO>.Unauthorize("You don't have permission to access");

            var loginUserId = loginUser.Id;
            var selectedItems = await _unitOfWork.CartRepository
                .GetQueryable((int)loginUserId)
                .Where(item => item.CreatedBy == loginUserId && item.IsSelected)
                .Include(item => item.IosDeviceNavigation)
                .Include(item => item.ComboNavigation)
                .Include(item => item.LabNavigation)
                .ToListAsync();

            string vnpayData = dto.urlResponse.Split("?")[1];
            VnPayLibrary vnpay = new VnPayLibrary();
            foreach (string s in vnpayData.Split("&"))
            {
                var parts = s.Split("=");
                if (parts.Length >= 2 && parts[0].StartsWith("vnp_"))
                {
                    vnpay.AddResponseData(parts[0], parts[1]);
                }
            }

            string vnp_ResponseCode = vnpay.GetResponseData("vnp_ResponseCode");
            string vnp_TransactionStatus = vnpay.GetResponseData("vnp_TransactionStatus");
            string encodedOrderInfo = vnpay.GetResponseData("vnp_OrderInfo");
            string txn_ref = vnpay.GetResponseData("vnp_TxnRef");

            string address = "", contactPhone = "", notes = "";
            int provinceId = 0, districtId = 0, wardId = 0, addressId = 0;
            string provinceName = "", districtName = "", wardName = "", fullAddress = "", deliver_option = "";

            var existingOrderItem = await _unitOfWork.OrderDetailRepository
            .GetQueryable()
            .FirstOrDefaultAsync(oi => oi.TxnRef == txn_ref);

            if (existingOrderItem != null)
            {
                return new GenericResponseDTO<OrderReturnPaymentDTO>
                {
                    IsSuccess = false,
                    Message = "Payment exits.",
                    Data = null
                };
            }

            if (!string.IsNullOrEmpty(encodedOrderInfo))
            {
                try
                {
                    string decodedUrl = HttpUtility.UrlDecode(encodedOrderInfo);
                    byte[] data = Convert.FromBase64String(decodedUrl);
                    string jsonString = Encoding.UTF8.GetString(data);
                    var orderInfo = JsonConvert.DeserializeObject<OrderInfo>(jsonString);

                    address = orderInfo?.Address ?? "";
                    addressId = orderInfo?.AddressId ?? 0;
                    contactPhone = orderInfo?.ContactNumber ?? "";
                    notes = orderInfo?.Notes ?? "";
                    provinceId = orderInfo?.ProvinceId ?? 0;
                    districtId = orderInfo?.DistrictId ?? 0;
                    wardId = orderInfo?.WardId ?? 0;
                    deliver_option = orderInfo?.deliver_option ?? "";
                }
                catch (FormatException)
                {
                    Console.WriteLine("Invalid Base64 format for vnp_OrderInfo. Using raw value.");
                }
            }

            if (vnp_ResponseCode != "00" || vnp_TransactionStatus != "00")
            {
                return new GenericResponseDTO<OrderReturnPaymentDTO>
                {
                    IsSuccess = false,
                    Message = "Payment Fails.",
                    Data = null
                };
            }

            var shippingFees = await _ghtkService.GetShippingFeeAsync(new ShippingFeeRequest
            {
                ProvinceId = provinceId,
                DistrictId = districtId,
                WardId = wardId,
                AddressId = addressId,
                Address = address,
                deliver_option = deliver_option
            });

            var createShipping = await _ghtkService.CreateShipmentAsync(new ShippingRequest
            {
                ProvinceId = provinceId,
                DistrictId = districtId,
                WardId = wardId,
                AddressId = addressId,
                Address = address,
                ContactNumber = contactPhone,
                note = notes
            });

            var totalShippingFee = shippingFees.FirstOrDefault(f => f.ShopOwnerId == -1)?.Fee ?? 0m;
            decimal totalProductPrice = (Convert.ToInt64(vnpay.GetResponseData("vnp_Amount")) / 100 - totalShippingFee);
            decimal totalAmount = (Convert.ToInt64(vnpay.GetResponseData("vnp_Amount")) / 100);
            TimeZoneInfo vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            DateTime vietnamTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
            var createTransactionPayment = new Orders
            {
                ApplicationSerialNumber = GetApplicationSerialNumberOrder(loginUserId),
                OrderBy = loginUserId,
                TotalPrice = totalAmount,
                Address = address,
                ProvinceId = provinceId,
                DistrictId = districtId,
                WardId = wardId,
                AddressId = addressId,
                ContactNumber = contactPhone,
                Notes = notes,
                CreateDate = vietnamTime,
                CreatedBy = loginUserId,
                UpdatedBy = loginUserId,
                ShippingFee = totalShippingFee,
                OrderStatusId = (int)OrderStatusEnum.SUCCESS_TO_ORDER
            };
            var orderResponse = _unitOfWork.OrderRepository.Create(createTransactionPayment);

            var saveOrderItems = new List<OrderItem>();

            foreach (var item in selectedItems)
            {
                var trackingId = createShipping.FirstOrDefault(s => s.ShopOwnerId == item.SellerId)?.TrackingId;

                var orderDetail = new OrderItem
                {
                    OrderId = createTransactionPayment.Id,
                    SellerId = item.SellerId,
                    OrderBy = loginUserId,
                    Quantity = item.Quantity,
                    ProductType = item.IosDeviceNavigation != null ? (int)ProductTypeEnum.IOT_DEVICE :
                                  item.ComboNavigation != null ? (int)ProductTypeEnum.COMBO :
                                  item.LabNavigation != null ? (int)ProductTypeEnum.LAB : 0,
                    IosDeviceId = item.IosDeviceNavigation?.Id,
                    ComboId = item.ComboNavigation?.Id,
                    LabId = item.LabNavigation?.Id,
                    Price = item.IosDeviceNavigation?.Price ?? item.ComboNavigation?.Price ?? item.LabNavigation?.Price ?? 0m,
                    OrderItemStatus = (int)OrderItemStatusEnum.PENDING,
                    TxnRef = vnpay.GetResponseData("vnp_TxnRef"),
                    TrackingId = trackingId
                    //WarrantyEndDate = DateTime.Now.AddMonths(item?.IosDeviceNavigation?.WarrantyMonth ?? item?.ComboNavigation?.WarrantyMonth ?? 0)
                };

                _unitOfWork.OrderDetailRepository.Create(orderDetail);

                saveOrderItems.Add(orderDetail);

                if (item.IosDeviceNavigation != null)
                {
                    var device = await _unitOfWork.IotsDeviceRepository.GetByIdAsync(item.IosDeviceNavigation.Id);
                    if (device != null)
                    {
                        device.Quantity -= item.Quantity;
                        if (device.Quantity < 0)
                            device.Quantity = 0; // Đảm bảo không bị âm

                        _unitOfWork.IotsDeviceRepository.Update(device);
                    }
                }

                if (item.ComboNavigation != null)
                {
                    var combo = await _unitOfWork.ComboRepository.GetByIdAsync(item.ComboNavigation.Id);
                    if (combo != null)
                    {
                        combo.Quantity -= item.Quantity;

                        if (combo.Quantity < 0)
                            combo.Quantity = 0;

                        _unitOfWork.ComboRepository.Update(combo);
                    }
                }

                if (item.LabNavigation != null)
                {
                    var lab = await _unitOfWork.LabRepository.GetByIdAsync(item.LabNavigation.Id);
                    if (lab != null)
                    {
                        orderDetail.OrderItemStatus = (int)OrderItemStatusEnum.PENDING_TO_FEEDBACK;
                        _unitOfWork.OrderDetailRepository.Update(orderDetail);
                    }
                }
            }

            var orderReturnPaymentDTO = new OrderReturnPaymentDTO
            {
                PaymentUrl = "The order has been successfully paid.",
                ApplicationSerialNumber = createTransactionPayment.ApplicationSerialNumber,
                TotalPrice = createTransactionPayment.TotalPrice,
                ProvinceId = createTransactionPayment.ProvinceId,
                DistrictId = createTransactionPayment.DistrictId,
                WardId = createTransactionPayment.WardId,
                AddressId = createTransactionPayment.AddressId,
                Address = createTransactionPayment.Address,
                ContactNumber = createTransactionPayment.ContactNumber,
                Notes = createTransactionPayment.Notes,
                CreateDate = createTransactionPayment.CreateDate,
                OrderStatusId = createTransactionPayment.OrderStatusId
            };

            var provinces = await _ghtkService.SyncProvincesAsync();
            var province = provinces.FirstOrDefault(p => p.Id == createTransactionPayment.ProvinceId);
            orderReturnPaymentDTO.ProvinceName = province?.Name ?? "Not found";

            var districts = await _ghtkService.SyncDistrictsAsync(createTransactionPayment.ProvinceId);
            var district = districts.FirstOrDefault(d => d.Id == createTransactionPayment.DistrictId);
            orderReturnPaymentDTO.DistrictName = district?.Name ?? "Not found";

            var wards = await _ghtkService.SyncWardsAsync(createTransactionPayment.DistrictId);
            var ward = wards.FirstOrDefault(w => w.Id == createTransactionPayment.WardId);
            orderReturnPaymentDTO.WardName = ward?.Name ?? "Not found";

            var list_address = await _ghtkService.SyncAddressAsync(createTransactionPayment.WardId);
            var addressName = list_address.FirstOrDefault(w => w.Id == createTransactionPayment.AddressId);
            orderReturnPaymentDTO.AddressName = addressName?.Name ?? "Not found";

            var productBillList = selectedItems.Select(item => new ProductBillDTO
            {
                Img = item.IosDeviceNavigation?.ImageUrl ?? item.ComboNavigation?.ImageUrl ?? item.LabNavigation?.ImageUrl ?? "https://photos.google.com/share/AF1QipOAm47U_r0mYVWWJxwxSLvEmX4pvf5A16824osh1cd76-QUAV0cie7Z4uoL-zvefg/photo/AF1QipPy0TZ1bvUJYlEuL5HC2ZTct16AVQh1IRbINQMq?key=YnZpSm1hTmtJUGhDa2E4Q2M0aHZDU0Jxd2F4MWhn",
                Name = item.IosDeviceNavigation?.Name ?? item.ComboNavigation?.Name ?? item.LabNavigation?.Title ?? "Product",
                Quantity = item.Quantity,
                Price = item.IosDeviceNavigation?.Price ?? item.ComboNavigation?.Price ?? item.LabNavigation?.Price ?? 0m,
            }).ToList();

            string provinceNameCustomer = province.Name;
            string districtNameCustomer = district.Name;
            string wardNameCustomer = ward.Name;
            string addressNameCustomer = addressName.Name;


            await _emailServices.SendInvoiceEmailAsync(
                loginUser.Email,
                createTransactionPayment.ApplicationSerialNumber,
                "IoT Materials Trading Platform",
                $"FPT University",
                "IoTs.admin@iots.com",
                loginUser.Fullname,
                provinceNameCustomer,
                districtNameCustomer,
                wardNameCustomer,
                addressNameCustomer,
                productBillList,
                totalProductPrice,
                totalShippingFee,
                totalAmount
            );

            await _unitOfWork.CartRepository.RemoveAsync(selectedItems);
            await _unitOfWork.CartRepository.SaveAsync();

            var customerNotification = new Notifications
            {
                EntityId = orderResponse.Id,
                EntityType = (int)EntityTypeConst.EntityTypeEnum.ORDER,
                CreatedDate = DateTime.Now,
                ReceiverId = loginUserId,
                Content = $"Your Order has been created successfully. Please go to the order history to check",
                Title = $"Your Order has been created successfully. Please go to the order history to check"
            };

            var storeNotifications = new List<Notifications>();

            //Create notification and transaction
            try
            {
                if (saveOrderItems != null)
                    storeNotifications = saveOrderItems?.Select(item => item.SellerId)?.Distinct()?.ToList()?.Select(
                        sellerId =>
                        {
                            return new Notifications
                            {
                                EntityId = orderResponse.Id,
                                EntityType = (int)EntityTypeConst.EntityTypeEnum.ORDER,
                                Content = $"You have new order. Please go to order history to check",
                                Title = $"You have new order. Please go to order history to check",
                                ReceiverId = sellerId
                            };
                        }
                    )?.ToList();

                var transaction = new Transaction
                {
                    UserId = loginUserId,
                    CreatedDate = DateTime.UtcNow.AddHours(7),
                    Description = $"You have deposited {orderResponse.TotalPrice} VND into the system.",
                    Amount = -(orderResponse.TotalPrice / 1000),
                    CurrentBallance = -1,
                    TransactionType = $"Order {orderResponse.ApplicationSerialNumber}",
                    Status = "Success",
                };

                //Transaction and notification
                _ = _unitOfWork.TransactionRepository.CreateAsync([transaction]);

                _ = _unitOfWork.NotificationRepository.Create(customerNotification);

                if (storeNotifications != null)
                    _ = _unitOfWork.NotificationRepository.CreateAsync(storeNotifications);
            }
            catch
            {

            }

            return new GenericResponseDTO<OrderReturnPaymentDTO>
            {
                IsSuccess = true,
                Data = orderReturnPaymentDTO
            };
        }

        public async Task<GenericResponseDTO<OrderReturnPaymentDTO>> CheckOrderSuccessfullByMobile(int? id, VNPayRequestDTO dto)
        {
            string vnpayData = dto.urlResponse.Split("?")[1];
            VnPayLibrary vnpay = new VnPayLibrary();
            foreach (string s in vnpayData.Split("&"))
            {
                var parts = s.Split("=");
                if (parts.Length >= 2 && parts[0].StartsWith("vnp_"))
                {
                    vnpay.AddResponseData(parts[0], parts[1]);
                }
            }

            string vnp_ResponseCode = vnpay.GetResponseData("vnp_ResponseCode");
            string vnp_TransactionStatus = vnpay.GetResponseData("vnp_TransactionStatus");
            string encodedOrderInfo = vnpay.GetResponseData("vnp_OrderInfo");
            string txn_ref = vnpay.GetResponseData("vnp_TxnRef");

            string address = "", contactPhone = "", notes = "";
            int provinceId = 0, districtId = 0, wardId = 0, addressId = 0, userId = 0;
            string provinceName = "", districtName = "", wardName = "", fullAddress = "", deliver_option = "";

            var existingOrderItem = await _unitOfWork.OrderDetailRepository
            .GetQueryable()
            .FirstOrDefaultAsync(oi => oi.TxnRef == txn_ref);

            if (existingOrderItem != null)
            {
                return new GenericResponseDTO<OrderReturnPaymentDTO>
                {
                    IsSuccess = false,
                    Message = "Payment exits.",
                    Data = null
                };
            }

            if (!string.IsNullOrEmpty(encodedOrderInfo))
            {
                try
                {
                    string decodedUrl = HttpUtility.UrlDecode(encodedOrderInfo);
                    byte[] data = Convert.FromBase64String(decodedUrl);
                    string jsonString = Encoding.UTF8.GetString(data);
                    var orderInfo = JsonConvert.DeserializeObject<OrderInfoByMobile>(jsonString);

                    userId = orderInfo?.UserId ?? 4;
                    address = orderInfo?.Address ?? "";
                    addressId = orderInfo?.AddressId ?? 0;
                    contactPhone = orderInfo?.ContactNumber ?? "";
                    notes = orderInfo?.Notes ?? "";
                    provinceId = orderInfo?.ProvinceId ?? 0;
                    districtId = orderInfo?.DistrictId ?? 0;
                    wardId = orderInfo?.WardId ?? 0;
                    deliver_option = orderInfo?.deliver_option ?? "";
                }
                catch (FormatException)
                {
                    Console.WriteLine("Invalid Base64 format for vnp_OrderInfo. Using raw value.");
                }
            }

            if (vnp_ResponseCode != "00" || vnp_TransactionStatus != "00")
            {
                return new GenericResponseDTO<OrderReturnPaymentDTO>
                {
                    IsSuccess = false,
                    Message = "Payment Fails.",
                    Data = null
                };
            }

            var loginUser = await _unitOfWork.UserRepository.GetUserById(userId);
            var loginUserId = userId;
            var selectedItems = await _unitOfWork.CartRepository
                .GetQueryable((int)loginUserId)
                .Where(item => item.CreatedBy == loginUserId && item.IsSelected)
                .Include(item => item.IosDeviceNavigation)
                .Include(item => item.ComboNavigation)
                .Include(item => item.LabNavigation)
                .ToListAsync();

            var shippingFees = await _ghtkService.GetShippingFeeByMobileAsync(new ShippingFeeRequestByMobile
            {
                UserId = loginUserId,
                ProvinceId = provinceId,
                DistrictId = districtId,
                WardId = wardId,
                AddressId = addressId,
                Address = address,
                deliver_option = deliver_option
            });

            var createShipping = await _ghtkService.CreateShipmentByMobileAsync(new ShippingRequestByMobile
            {
                UserId = loginUserId,
                ProvinceId = provinceId,
                DistrictId = districtId,
                WardId = wardId,
                AddressId = addressId,
                Address = address,
                ContactNumber = contactPhone,
                note = notes
            });

            var totalShippingFee = shippingFees.FirstOrDefault(f => f.ShopOwnerId == -1)?.Fee ?? 0m;
            decimal totalProductPrice = (Convert.ToInt64(vnpay.GetResponseData("vnp_Amount")) / 100 - totalShippingFee);
            decimal totalAmount = (Convert.ToInt64(vnpay.GetResponseData("vnp_Amount")) / 100);
            TimeZoneInfo vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            DateTime vietnamTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
            var createTransactionPayment = new Orders
            {
                ApplicationSerialNumber = GetApplicationSerialNumberOrder(loginUserId),
                OrderBy = loginUserId,
                TotalPrice = totalAmount,
                Address = address,
                ProvinceId = provinceId,
                DistrictId = districtId,
                WardId = wardId,
                AddressId = addressId,
                ContactNumber = contactPhone,
                Notes = notes,
                CreateDate = vietnamTime,
                CreatedBy = loginUserId,
                UpdatedBy = loginUserId,
                ShippingFee = totalShippingFee,
                OrderStatusId = (int)OrderStatusEnum.SUCCESS_TO_ORDER
            };
            var orderResponse = _unitOfWork.OrderRepository.Create(createTransactionPayment);

            var saveOrderItems = new List<OrderItem>();

            foreach (var item in selectedItems)
            {
                var trackingId = createShipping.FirstOrDefault(s => s.ShopOwnerId == item.SellerId)?.TrackingId;

                var orderDetail = new OrderItem
                {
                    OrderId = createTransactionPayment.Id,
                    SellerId = item.SellerId,
                    OrderBy = loginUserId,
                    Quantity = item.Quantity,
                    ProductType = item.IosDeviceNavigation != null ? (int)ProductTypeEnum.IOT_DEVICE :
                                  item.ComboNavigation != null ? (int)ProductTypeEnum.COMBO :
                                  item.LabNavigation != null ? (int)ProductTypeEnum.LAB : 0,
                    IosDeviceId = item.IosDeviceNavigation?.Id,
                    ComboId = item.ComboNavigation?.Id,
                    LabId = item.LabNavigation?.Id,
                    Price = item.IosDeviceNavigation?.Price ?? item.ComboNavigation?.Price ?? item.LabNavigation?.Price ?? 0m,
                    OrderItemStatus = (int)OrderItemStatusEnum.PENDING,
                    TxnRef = vnpay.GetResponseData("vnp_TxnRef"),
                    TrackingId = trackingId
                    //WarrantyEndDate = DateTime.Now.AddMonths(item?.IosDeviceNavigation?.WarrantyMonth ?? item?.ComboNavigation?.WarrantyMonth ?? 0)
                };

                _unitOfWork.OrderDetailRepository.Create(orderDetail);

                saveOrderItems.Add(orderDetail);

                if (item.IosDeviceNavigation != null)
                {
                    var device = await _unitOfWork.IotsDeviceRepository.GetByIdAsync(item.IosDeviceNavigation.Id);
                    if (device != null)
                    {
                        device.Quantity -= item.Quantity;
                        if (device.Quantity < 0)
                            device.Quantity = 0; // Đảm bảo không bị âm

                        _unitOfWork.IotsDeviceRepository.Update(device);
                    }
                }

                if (item.ComboNavigation != null)
                {
                    var combo = await _unitOfWork.ComboRepository.GetByIdAsync(item.ComboNavigation.Id);
                    if (combo != null)
                    {
                        combo.Quantity -= item.Quantity;

                        if (combo.Quantity < 0)
                            combo.Quantity = 0;

                        _unitOfWork.ComboRepository.Update(combo);
                    }
                }

                if (item.LabNavigation != null)
                {
                    var lab = await _unitOfWork.LabRepository.GetByIdAsync(item.LabNavigation.Id);
                    if (lab != null)
                    {
                        orderDetail.OrderItemStatus = (int)OrderItemStatusEnum.PENDING_TO_FEEDBACK;
                        _unitOfWork.OrderDetailRepository.Update(orderDetail);
                    }
                }
            }

            var orderReturnPaymentDTO = new OrderReturnPaymentDTO
            {
                PaymentUrl = "The order has been successfully paid.",
                ApplicationSerialNumber = createTransactionPayment.ApplicationSerialNumber,
                TotalPrice = createTransactionPayment.TotalPrice,
                ProvinceId = createTransactionPayment.ProvinceId,
                DistrictId = createTransactionPayment.DistrictId,
                WardId = createTransactionPayment.WardId,
                AddressId = createTransactionPayment.AddressId,
                Address = createTransactionPayment.Address,
                ContactNumber = createTransactionPayment.ContactNumber,
                Notes = createTransactionPayment.Notes,
                CreateDate = createTransactionPayment.CreateDate,
                OrderStatusId = createTransactionPayment.OrderStatusId
            };

            var provinces = await _ghtkService.SyncProvincesAsync();
            var province = provinces.FirstOrDefault(p => p.Id == createTransactionPayment.ProvinceId);
            orderReturnPaymentDTO.ProvinceName = province?.Name ?? "Not found";

            var districts = await _ghtkService.SyncDistrictsAsync(createTransactionPayment.ProvinceId);
            var district = districts.FirstOrDefault(d => d.Id == createTransactionPayment.DistrictId);
            orderReturnPaymentDTO.DistrictName = district?.Name ?? "Not found";

            var wards = await _ghtkService.SyncWardsAsync(createTransactionPayment.DistrictId);
            var ward = wards.FirstOrDefault(w => w.Id == createTransactionPayment.WardId);
            orderReturnPaymentDTO.WardName = ward?.Name ?? "Not found";

            var list_address = await _ghtkService.SyncAddressAsync(createTransactionPayment.WardId);
            var addressName = list_address.FirstOrDefault(w => w.Id == createTransactionPayment.AddressId);
            orderReturnPaymentDTO.AddressName = addressName?.Name ?? "Not found";

            var productBillList = selectedItems.Select(item => new ProductBillDTO
            {
                Img = item.IosDeviceNavigation?.ImageUrl ?? item.ComboNavigation?.ImageUrl ?? item.LabNavigation?.ImageUrl ?? "https://photos.google.com/share/AF1QipOAm47U_r0mYVWWJxwxSLvEmX4pvf5A16824osh1cd76-QUAV0cie7Z4uoL-zvefg/photo/AF1QipPy0TZ1bvUJYlEuL5HC2ZTct16AVQh1IRbINQMq?key=YnZpSm1hTmtJUGhDa2E4Q2M0aHZDU0Jxd2F4MWhn",
                Name = item.IosDeviceNavigation?.Name ?? item.ComboNavigation?.Name ?? item.LabNavigation?.Title ?? "Product",
                Quantity = item.Quantity,
                Price = item.IosDeviceNavigation?.Price ?? item.ComboNavigation?.Price ?? item.LabNavigation?.Price ?? 0m,
            }).ToList();

            string provinceNameCustomer = province.Name;
            string districtNameCustomer = district.Name;
            string wardNameCustomer = ward.Name;
            string addressNameCustomer = addressName.Name;


            await _emailServices.SendInvoiceEmailAsync(
                loginUser.Email,
                createTransactionPayment.ApplicationSerialNumber,
                "IoT Materials Trading Platform",
                $"FPT University",
                "IoTs.admin@iots.com",
                loginUser.Fullname,
                provinceNameCustomer,
                districtNameCustomer,
                wardNameCustomer,
                addressNameCustomer,
                productBillList,
                totalProductPrice,
                totalShippingFee,
                totalAmount
            );

            await _unitOfWork.CartRepository.RemoveAsync(selectedItems);
            await _unitOfWork.CartRepository.SaveAsync();

            var customerNotification = new Notifications
            {
                EntityId = orderResponse.Id,
                EntityType = (int)EntityTypeConst.EntityTypeEnum.ORDER,
                CreatedDate = DateTime.Now,
                ReceiverId = loginUserId,
                Content = $"Your Order has been created successfully. Please go to the order history to check",
                Title = $"Your Order has been created successfully. Please go to the order history to check"
            };

            var storeNotifications = new List<Notifications>();

            //Create notification and transaction
            try
            {
                if (saveOrderItems != null)
                    storeNotifications = saveOrderItems?.Select(item => item.SellerId)?.Distinct()?.ToList()?.Select(
                        sellerId =>
                        {
                            return new Notifications
                            {
                                EntityId = orderResponse.Id,
                                EntityType = (int)EntityTypeConst.EntityTypeEnum.ORDER,
                                Content = $"You have new order. Please go to order history to check",
                                Title = $"You have new order. Please go to order history to check",
                                ReceiverId = sellerId
                            };
                        }
                    )?.ToList();

                var transaction = new Transaction
                {
                    UserId = loginUserId,
                    CreatedDate = DateTime.Now,
                    Description = $"You have deposited {orderResponse.TotalPrice} VND into the system.",
                    Amount = orderResponse.TotalPrice,
                    CurrentBallance = -1,
                    TransactionType = "Order",
                    Status = "Success",
                };

                //Transaction and notification
                _ = _unitOfWork.TransactionRepository.CreateAsync([transaction]);

                _ = _unitOfWork.NotificationRepository.Create(customerNotification);

                if (storeNotifications != null)
                    _ = _unitOfWork.NotificationRepository.CreateAsync(storeNotifications);
            }
            catch
            {

            }

            return new GenericResponseDTO<OrderReturnPaymentDTO>
            {
                IsSuccess = true,
                Data = orderReturnPaymentDTO
            };
        }


        public async Task<ResponseDTO> CreateCashPaymentOrder(OrderRequestDTO payload)
        {
            var loginUser = userServices.GetLoginUser();
            if (loginUser == null || !await userServices.CheckLoginUserRole(RoleEnum.CUSTOMER))
                return ResponseService<object>.Unauthorize("You don't have permission to access");

            var loginUserId = loginUser.Id;
            var selectedItems = await _unitOfWork.CartRepository
                .GetQueryable((int)loginUserId)
                .Where(item => item.CreatedBy == loginUserId && item.IsSelected)
                .Include(item => item.IosDeviceNavigation)
                .Include(item => item.ComboNavigation)
                .Include(item => item.LabNavigation)
                .ToListAsync();

            //Including any labs?
            //if (selectedItems.Any(item => item.LabId != null))
            //{
            //    return ResponseService<object>.BadRequest("You are not allow to use cash payment method to buy lab. Please using Online Payment");
            //}

            string address = payload.Address, contactPhone = payload.ContactNumber, notes = payload.Notes;
            int provinceId = payload.ProvinceId, districtId = payload.DistrictId, wardId = payload.WardId, addressId = payload.AddressId;
            string deliver_option = payload.deliver_option;

            var shippingFees = await _ghtkService.GetShippingFeeAsync(new ShippingFeeRequest
            {
                ProvinceId = provinceId,
                DistrictId = districtId,
                WardId = wardId,
                AddressId = addressId,
                Address = address,
                deliver_option = deliver_option
            });

            var createShipping = await _ghtkService.CreateShipmentHasShipCodAsync(new ShippingRequest
            {
                ProvinceId = provinceId,
                DistrictId = districtId,
                WardId = wardId,
                AddressId = addressId,
                Address = address,
                ContactNumber = contactPhone,
                note = notes
            });

            var totalShippingFee = shippingFees.FirstOrDefault(f => f.ShopOwnerId == -1)?.Fee ?? 0m;
            decimal totalProductPrice = selectedItems.Sum(
                item =>
                {
                    decimal productPrice = item?.IosDeviceNavigation?.Price ?? item?.ComboNavigation?.Price ?? item?.LabNavigation?.Price ?? 0;

                    decimal totalAmount = productPrice * (item?.Quantity ?? 0);

                    return totalAmount;
                }
            );

            decimal totalAmount = totalProductPrice + totalShippingFee;

            TimeZoneInfo vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            DateTime vietnamTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);

            var createTransactionPayment = new Orders
            {
                ApplicationSerialNumber = GetApplicationSerialNumberOrder(loginUserId),
                OrderBy = loginUserId,
                TotalPrice = totalAmount,
                Address = address,
                ProvinceId = provinceId,
                DistrictId = districtId,
                WardId = wardId,
                AddressId = addressId,
                ContactNumber = contactPhone,
                Notes = notes,
                CreateDate = vietnamTime,
                CreatedBy = loginUserId,
                UpdatedBy = loginUserId,
                ShippingFee = totalShippingFee,
                OrderStatusId = (int)OrderStatusEnum.CASH_PAYMENT
            };
            var orderResponse = _unitOfWork.OrderRepository.Create(createTransactionPayment);

            var saveOrderItems = new List<OrderItem>();

            foreach (var item in selectedItems)
            {
                var trackingId = createShipping.FirstOrDefault(s => s.ShopOwnerId == item.SellerId)?.TrackingId;

                var orderDetail = new OrderItem
                {
                    OrderId = createTransactionPayment.Id,
                    SellerId = item.SellerId,
                    OrderBy = loginUserId,
                    Quantity = item.Quantity,
                    ProductType = item.IosDeviceNavigation != null ? (int)ProductTypeEnum.IOT_DEVICE :
                                  item.ComboNavigation != null ? (int)ProductTypeEnum.COMBO :
                                  item.LabNavigation != null ? (int)ProductTypeEnum.LAB : throw new Exception("Invalid product type"),
                    IosDeviceId = item.IosDeviceNavigation?.Id,
                    ComboId = item.ComboNavigation?.Id,
                    LabId = item.LabNavigation?.Id,
                    Price = item.IosDeviceNavigation?.Price ?? item.ComboNavigation?.Price ?? item.LabNavigation?.Price ?? 0m,
                    OrderItemStatus = (int)OrderItemStatusEnum.PENDING,
                    TxnRef = "",
                    TrackingId = trackingId,
                    WarrantyEndDate = DateTime.UtcNow.AddHours(7).AddMonths(item?.IosDeviceNavigation?.WarrantyMonth ?? item?.ComboNavigation?.WarrantyMonth ?? 0)
                };

                _unitOfWork.OrderDetailRepository.Create(orderDetail);

                saveOrderItems.Add(orderDetail);

                if (item?.IosDeviceNavigation != null)
                {
                    var device = await _unitOfWork.IotsDeviceRepository.GetByIdAsync(item.IosDeviceNavigation.Id);
                    if (device != null)
                    {
                        device.Quantity -= item.Quantity;
                        if (device.Quantity < 0)
                            device.Quantity = 0;

                        _unitOfWork.IotsDeviceRepository.Update(device);
                    }
                }

                if (item?.ComboNavigation != null)
                {
                    var combo = await _unitOfWork.ComboRepository.GetByIdAsync(item.ComboNavigation.Id);
                    if (combo != null)
                    {
                        combo.Quantity -= item.Quantity;

                        if (combo.Quantity < 0)
                            combo.Quantity = 0;

                        _unitOfWork.ComboRepository.Update(combo);
                    }
                }

                if (item?.LabNavigation != null)
                {
                    var lab = await _unitOfWork.LabRepository.GetByIdAsync(item.LabNavigation.Id);
                    if (lab != null)
                    {
                        orderDetail.OrderItemStatus = (int)OrderItemStatusEnum.PENDING_TO_FEEDBACK;
                        _unitOfWork.OrderDetailRepository.Update(orderDetail);
                    }
                }
            }

            var orderReturnPaymentDTO = new OrderReturnPaymentDTO
            {
                PaymentUrl = "The order has been successfully paid.",
                ApplicationSerialNumber = createTransactionPayment.ApplicationSerialNumber,
                TotalPrice = createTransactionPayment.TotalPrice,
                ProvinceId = createTransactionPayment.ProvinceId,
                DistrictId = createTransactionPayment.DistrictId,
                WardId = createTransactionPayment.WardId,
                AddressId = createTransactionPayment.AddressId,
                Address = createTransactionPayment.Address,
                ContactNumber = createTransactionPayment.ContactNumber,
                Notes = createTransactionPayment?.Notes ?? "",
                CreateDate = createTransactionPayment?.CreateDate ?? DateTime.Now,
                OrderStatusId = createTransactionPayment.OrderStatusId
            };

            var provinces = await _ghtkService.SyncProvincesAsync();
            var province = provinces.FirstOrDefault(p => p.Id == createTransactionPayment.ProvinceId);
            orderReturnPaymentDTO.ProvinceName = province?.Name ?? "Not found";

            var districts = await _ghtkService.SyncDistrictsAsync(createTransactionPayment.ProvinceId);
            var district = districts.FirstOrDefault(d => d.Id == createTransactionPayment.DistrictId);
            orderReturnPaymentDTO.DistrictName = district?.Name ?? "Not found";

            var wards = await _ghtkService.SyncWardsAsync(createTransactionPayment.DistrictId);
            var ward = wards.FirstOrDefault(w => w.Id == createTransactionPayment.WardId);
            orderReturnPaymentDTO.WardName = ward?.Name ?? "Not found";

            var list_address = await _ghtkService.SyncAddressAsync(createTransactionPayment.WardId);
            var addressName = list_address.FirstOrDefault(w => w.Id == createTransactionPayment.AddressId);
            orderReturnPaymentDTO.AddressName = addressName?.Name ?? "Not found";

            var productBillList = selectedItems.Select(item => new ProductBillDTO
            {
                Img = item.IosDeviceNavigation?.ImageUrl ?? item.ComboNavigation?.ImageUrl ?? item.LabNavigation?.ImageUrl ?? "https://photos.google.com/share/AF1QipOAm47U_r0mYVWWJxwxSLvEmX4pvf5A16824osh1cd76-QUAV0cie7Z4uoL-zvefg/photo/AF1QipPy0TZ1bvUJYlEuL5HC2ZTct16AVQh1IRbINQMq?key=YnZpSm1hTmtJUGhDa2E4Q2M0aHZDU0Jxd2F4MWhn",
                Name = item.IosDeviceNavigation?.Name ?? item.ComboNavigation?.Name ?? item.LabNavigation?.Title ?? "Product",
                Quantity = item.Quantity,
                Price = item.IosDeviceNavigation?.Price ?? item.ComboNavigation?.Price ?? item.LabNavigation?.Price ?? 0m,
            }).ToList();

            string provinceNameCustomer = province.Name;
            string districtNameCustomer = district.Name;
            string wardNameCustomer = ward.Name;
            string addressNameCustomer = addressName.Name;


            await _emailServices.SendInvoiceEmailAsync(
                loginUser.Email,
                createTransactionPayment.ApplicationSerialNumber,
                "IoT Materials Trading Platform",
                $"FPT University",
                "IoTs.admin@iots.com",
                loginUser.Fullname,
                provinceNameCustomer,
                districtNameCustomer,
                wardNameCustomer,
                addressNameCustomer,
                productBillList,
                totalProductPrice,
                totalShippingFee,
                totalAmount
            );

            await _unitOfWork.CartRepository.RemoveAsync(selectedItems);
            await _unitOfWork.CartRepository.SaveAsync();

            var customerNotification = new Notifications
            {
                EntityId = orderResponse.Id,
                EntityType = (int)EntityTypeConst.EntityTypeEnum.ORDER,
                CreatedDate = DateTime.Now,
                ReceiverId = loginUserId,
                Content = $"Your Order has been created successfully. Please go to the order history to check",
                Title = $"Your Order has been created successfully. Please go to the order history to check"
            };

            var storeNotifications = new List<Notifications>();

            //Create notification and transaction
            try
            {
                if (saveOrderItems != null)
                    storeNotifications = saveOrderItems?.Select(item => item.SellerId)?.Distinct()?.ToList()?.Select(
                        sellerId =>
                        {
                            return new Notifications
                            {
                                EntityId = orderResponse.Id,
                                EntityType = (int)EntityTypeConst.EntityTypeEnum.ORDER,
                                Content = $"You have new order. Please go to order history to check",
                                Title = $"You have new order. Please go to order history to check",
                                ReceiverId = sellerId
                            };
                        }
                    )?.ToList();

                _ = _unitOfWork.NotificationRepository.Create(customerNotification);

                if (storeNotifications != null)
                    _ = _unitOfWork.NotificationRepository.CreateAsync(storeNotifications);
            }
            catch
            {

            }

            return ResponseService<object>.OK(orderReturnPaymentDTO);
        }


        public async Task<GenericResponseDTO<OrderReturnPaymentVNPayDTO>> CreateOrder(int? id, OrderRequestDTO payload, string returnUrl)
        {
            try
            {
                var loginUser = userServices.GetLoginUser();

                if (loginUser == null || !await userServices.CheckLoginUserRole(RoleEnum.CUSTOMER))
                    return ResponseService<OrderReturnPaymentVNPayDTO>.Unauthorize("You don't have permission to access");

                var loginUserId = loginUser.Id;

                var address = payload.Address;
                var contactPhone = payload.ContactNumber;
                var notes = payload.Notes;
                if (address.Length > 70)
                {
                    throw new ArgumentException("Address cannot exceed 70 characters.");
                }

                if (!Regex.IsMatch(contactPhone, @"^0\d{9}$"))
                {
                    throw new ArgumentException("Contact phone must be a 10-digit number starting with 0.");
                }

                if (notes.Length > 100)
                {
                    throw new ArgumentException("Notes cannot exceed 100 characters.");
                }

                var provinces = await _ghtkService.SyncProvincesAsync();
                var province = provinces.FirstOrDefault(p => p.Id == payload.ProvinceId);
                if (province == null)
                    return ResponseService<OrderReturnPaymentVNPayDTO>.BadRequest("Invalid Province ID");

                var districts = await _ghtkService.SyncDistrictsAsync(payload.ProvinceId);
                var district = districts.FirstOrDefault(d => d.Id == payload.DistrictId);
                if (district == null)
                    return ResponseService<OrderReturnPaymentVNPayDTO>.BadRequest("Invalid District ID");

                var wards = await _ghtkService.SyncWardsAsync(payload.DistrictId);
                var ward = wards.FirstOrDefault(w => w.Id == payload.WardId);
                if (ward == null)
                    return ResponseService<OrderReturnPaymentVNPayDTO>.BadRequest("Invalid Ward ID");

                var addresses = await _ghtkService.SyncAddressAsync(payload.WardId);
                var addressid = addresses.FirstOrDefault(w => w.Id == payload.AddressId);
                if (addressid == null)
                    return ResponseService<OrderReturnPaymentVNPayDTO>.BadRequest("Invalid Address ID");

                // Get the list of selected products in the cart
                var selectedItems = await _unitOfWork.CartRepository
                    .GetQueryable((int)loginUserId)
                    .Where(item => item.CreatedBy == loginUserId && item.IsSelected)
                    .Include(item => item.IosDeviceNavigation)
                    .Include(item => item.ComboNavigation)
                    .Include(item => item.LabNavigation)
                    .ToListAsync();

                if (selectedItems == null || !selectedItems.Any())
                {
                    return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                    {
                        IsSuccess = false,
                        Message = "The cart is empty or no products have been selected.",
                        Data = null
                    };
                }

                var totalPrice = selectedItems.Sum(item =>
                    (((item.IosDeviceNavigation?.Price ?? 0m) * item.Quantity) + ((item.ComboNavigation?.Price ?? 0m) * item.Quantity)
                    + ((item.LabNavigation?.Price ?? 0m) * item.Quantity)
                    )
                );

                if (totalPrice <= 0)
                {
                    return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                    {
                        IsSuccess = false,
                        Message = "Invalid order value.",
                        Data = null
                    };
                }

                foreach (var item in selectedItems)
                {
                    if (item.IosDeviceNavigation != null)
                    {
                        var device = await _unitOfWork.IotsDeviceRepository.GetByIdAsync(item.IosDeviceNavigation.Id);
                        if (device == null || device.Quantity < item.Quantity)
                        {
                            return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                            {
                                IsSuccess = false,
                                Message = $"Product {device?.Name ?? "Unknown"} is out of stock.",
                                Data = null
                            };
                        }
                    }
                    if (item.ComboNavigation != null)
                    {
                        var combo = await _unitOfWork.ComboRepository.GetByIdAsync(item.ComboNavigation.Id);
                        if (combo == null || combo.Quantity < item.Quantity)
                        {
                            return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                            {
                                IsSuccess = false,
                                Message = $"Combo {combo?.Name ?? "Unknown"} is out of stock.",
                                Data = null
                            };
                        }
                    }
                }

                var shippingFees = await _ghtkService.GetShippingFeeAsync(new ShippingFeeRequest
                {
                    ProvinceId = payload.ProvinceId,
                    DistrictId = payload.DistrictId,
                    WardId = payload.WardId,
                    AddressId = payload.AddressId,
                    Address = payload.Address,
                    deliver_option = payload.deliver_option
                });

                var totalShippingFee = shippingFees.FirstOrDefault(f => f.ShopOwnerId == -1)?.Fee ?? 0m;

                var finalTotalPrice = totalPrice + totalShippingFee;

                var orderInfo = new OrderInfo
                {
                    Address = address,
                    ContactNumber = contactPhone,
                    Notes = notes,
                    ProvinceId = province.Id,
                    DistrictId = district.Id,
                    WardId = ward.Id,
                    AddressId = addressid.Id,
                    deliver_option = payload.deliver_option
                };

                string vnp_ReturnUrl = !string.IsNullOrEmpty(returnUrl) ? returnUrl : "https://localhost:44346/checkout-process-order";
                string vnp_Url = "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html";
                string vnp_TmnCode = "PJLU0FHO";
                string vnp_HashSecret = "4RY7BQN7ED5YFS7YR4TS3YONAJPGYYFL";

                // Convert orderInfo to JSON
                string orderInfoJson = JsonConvert.SerializeObject(orderInfo);
                Console.WriteLine("OrderInfo JSON: " + orderInfoJson);

                // Encode to Base64
                string encryptedOrderInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes(orderInfoJson));
                Console.WriteLine("Encoded OrderInfo (Base64): " + encryptedOrderInfo);
                if (orderInfoJson.Length > 255) // VNPay may have a 255-character limit
                {
                    Console.WriteLine("OrderInfo is too long, needs to be shortened!");
                }
                // Decode back to verify
                try
                {
                    string decodedOrderInfo = Encoding.UTF8.GetString(Convert.FromBase64String(encryptedOrderInfo));
                    Console.WriteLine("Decoded OrderInfo: " + decodedOrderInfo);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error when decoding Base64: " + ex.Message);
                }

                if (string.IsNullOrEmpty(vnp_TmnCode) || string.IsNullOrEmpty(vnp_HashSecret))
                {
                    throw new Exception("Merchant code or secret key is missing.");
                }

                // Generate transaction reference number
                var vnp_TxnRef = $"{loginUserId}{DateTime.Now:yyyyMMddHHmmssfff}{new Random().Next(1000, 9999)}";

                long vnp_Amount = (long)(finalTotalPrice * 100);
                if (finalTotalPrice <= 0)
                {
                    return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                    {
                        IsSuccess = false,
                        Message = "Invalid order value.",
                        Data = null
                    };
                }

                var vnpay = new VnPayLibrary();
                vnpay.AddRequestData("vnp_Version", "2.1.0");
                vnpay.AddRequestData("vnp_Command", "pay");
                vnpay.AddRequestData("vnp_TmnCode", vnp_TmnCode);
                vnpay.AddRequestData("vnp_Amount", vnp_Amount.ToString());
                TimeZoneInfo vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                DateTime vietnamTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
                vnpay.AddRequestData("vnp_CreateDate", vietnamTime.AddMinutes(-20).ToString("yyyyMMddHHmmss"));
                vnpay.AddRequestData("vnp_CurrCode", "VND");
                vnpay.AddRequestData("vnp_IpAddr", Utils.GetIpAddress());
                vnpay.AddRequestData("vnp_Locale", "vn");
                vnpay.AddRequestData("vnp_OrderInfo", encryptedOrderInfo);
                vnpay.AddRequestData("vnp_OrderType", "order");
                vnpay.AddRequestData("vnp_ReturnUrl", vnp_ReturnUrl);
                vnpay.AddRequestData("vnp_TxnRef", vnp_TxnRef);
                vnpay.AddRequestData("vnp_ExpireDate", vietnamTime.AddMinutes(5).ToString("yyyyMMddHHmmss"));
                string paymentUrl = vnpay.CreateRequestUrl(vnp_Url, vnp_HashSecret);

                return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                {
                    IsSuccess = true,
                    Message = "Order created successfully, please proceed with payment.",
                    Data = new OrderReturnPaymentVNPayDTO
                    {
                        PaymentUrl = paymentUrl
                    }
                };
            }
            catch (Exception ex)
            {
                throw new Exception($"An error occurred during payment: {ex.Message}");
            }
        }

        private string ReturnedErrorMessageResponseCode(string code)
        {
            switch (code)
            {
                case "00": return "Giao dịch thành công";
                case "07": return "Trừ tiền thành công. Giao dịch bị nghi ngờ (liên quan tới lừa đảo, giao dịch bất thường).";
                case "09": return "Giao dịch không thành công do: Thẻ/Tài khoản của khách hàng chưa đăng ký dịch vụ InternetBanking tại ngân hàng.";
                case "10": return "Giao dịch không thành công do: Khách hàng xác thực thông tin thẻ/tài khoản không đúng quá 3 lần.";
                case "11": return "Giao dịch không thành công do: Đã hết hạn chờ thanh toán. Xin quý khách vui lòng thực hiện lại giao dịch.";
                case "12": return "Giao dịch không thành công do: Thẻ/Tài khoản của khách hàng bị khóa.";
                case "13": return "Giao dịch không thành công do Quý khách nhập sai mật khẩu xác thực giao dịch (OTP). Xin quý khách vui lòng thực hiện lại giao dịch.";
                case "24": return "Giao dịch không thành công do: Khách hàng hủy giao dịch.";
                case "51": return "Giao dịch không thành công do: Tài khoản của quý khách không đủ số dư để thực hiện giao dịch.";
                case "65": return "Giao dịch không thành công do: Tài khoản của Quý khách đã vượt quá hạn mức giao dịch trong ngày.";
                case "75": return "Ngân hàng thanh toán đang bảo trì.";
                case "79": return "Giao dịch không thành công do: KH nhập sai mật khẩu thanh toán quá số lần quy định. Xin quý khách vui lòng thực hiện lại giao dịch.";
                case "99": return "Các lỗi khác (lỗi còn lại, không có trong danh sách mã lỗi đã liệt kê).";
                default: return "Mã lỗi không hợp lệ";
            }
        }

        private string ReturnedErrorMessageTransactionStatus(string code)
        {
            switch (code)
            {
                case "00": return "Giao dịch thành công";
                case "01": return "Giao dịch chưa hoàn tất";
                case "02": return "Giao dịch bị lỗi";
                case "04": return "Giao dịch đảo (Khách hàng đã bị trừ tiền tại Ngân hàng nhưng GD chưa thành công ở VNPAY)";
                case "05": return "VNPAY đang xử lý giao dịch này (GD hoàn tiền)";
                case "06": return "VNPAY đã gửi yêu cầu hoàn tiền sang Ngân hàng (GD hoàn tiền)";
                case "07": return "Giao dịch bị nghi ngờ gian lận";
                case "09": return "GD Hoàn trả bị từ chối";
                default: return "Mã lỗi không hợp lệ";
            }
        }

        public OrderResponseDTO BuildToOrderResponseDTO(IMapService<Orders, OrderResponseDTO> mapper,
            Orders order,
            Func<OrderItem, bool> orderItemFunc)
        {
            var res = mapper.MappingTo(order);

            res.OrderId = order.Id;

            var query = order.OrderItems
                .Where(orderItemFunc);

            var totalCount = query.Select(i => i.SellerId).Count();

            var pendingNumber = query
                .Where(i => i.OrderItemStatus == (int)OrderItemStatusEnum.PENDING)
                .Select(i => i.SellerId).Count();

            var deliveringNumber = query
                .Where(i => i.OrderItemStatus == (int)OrderItemStatusEnum.DELEVERING)
                .Select(i => i.SellerId).Count();

            var pendingToFeedbackNumber = query
                .Where(i => i.OrderItemStatus == (int)OrderItemStatusEnum.PENDING_TO_FEEDBACK)
                .Select(i => i.SellerId).Count();

            var badFeedbackNumber = query
                .Where(i => i.OrderItemStatus == (int)OrderItemStatusEnum.BAD_FEEDBACK)
                .Select(i => i.SellerId).Count();

            res.TotalCount = totalCount;
            res.PendingNumber = pendingNumber;
            res.PendingToFeedbackNumber = pendingToFeedbackNumber;
            res.DeliveringNumber = deliveringNumber;
            res.BadFeedbackNumber = badFeedbackNumber;

            return res;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="orderId"></param>
        /// <param name="orderExpress"></param>
        /// <param name="orderItemFunc"></param>
        /// <returns></returns>
        public async Task<GenericResponseDTO<OrderResponseDTO>> GetOrderIncludedGroupOrderItems(
            Expression<Func<Orders, bool>> orderExpress,
            Func<OrderItem, bool> orderItemFunc,
            OrderItemStatusEnum? orderItemStatusFilter = null)
        {
            var loginUserId = userServices.GetLoginUserId();

            var paginatedOrders = _unitOfWork.OrderRepository.GetPaginate(
                filter: orderExpress,
                orderBy: ob => ob.OrderByDescending(item => item.CreateDate),
                includeProperties: "OrderItems,OrderItems.IotsDevice,OrderItems.Combo,OrderItems.Lab,OrderItems.Lab.CreatedByNavigation",
                pageIndex: 0,
                pageSize: 1
            );

            var order = paginatedOrders?.Data?.FirstOrDefault();

            if (order == null)
                return ResponseService<OrderResponseDTO>.NotFound("No orders found for this user.");

            var sellerIds = paginatedOrders?.Data?.SelectMany(item => item.OrderItems.Select(o => o.SellerId)).Distinct();

            var storeList = _unitOfWork.StoreRepository.Search(
                item => (sellerIds != null) && sellerIds.Contains(item.OwnerId)
            ).Include(store => store.Owner).ToList();

            var mapper = new MapService<Orders, OrderResponseDTO>();

            // Build to Order Response DTO

            var res = BuildToOrderResponseDTO(mapper, order, orderItemFunc);

            //Group by seller id
            var orderDetailsGrouped = order.OrderItems
            .Where(orderItemFunc)
            .Where(orderItem => orderItemStatusFilter == null || orderItem.OrderItemStatus == (int)orderItemStatusFilter)
            .GroupBy(od => od.SellerId)
            .Select(group =>
            {
                var od = group?.FirstOrDefault();

                var sellerId = od?.SellerId;
                var trackingId = od?.TrackingId;
                var store = storeList.FirstOrDefault(s => s.OwnerId == sellerId);
                var trainer = od?.Lab?.CreatedByNavigation;
                var orderItemStatusId = od.OrderItemStatus;
                var warrantyMonths = od?.IotsDevice?.WarrantyMonth ?? od?.Combo?.WarrantyMonth ?? 0;

                var items = group?.Select(od => new OrderItemResponseDTO
                {
                    OrderItemId = od.Id,
                    ProductId = od?.IosDeviceId ?? od?.LabId ?? od?.ComboId,
                    NameProduct = od?.IotsDevice?.Name ?? od?.Combo?.Name ?? od?.Lab?.Title,
                    ProductType = od.ProductType,
                    ImageUrl = od?.IotsDevice?.ImageUrl ?? od?.Combo?.ImageUrl ?? od?.Lab?.ImageUrl,
                    Quantity = od.Quantity,
                    Price = od.Price,
                    WarrantyMonths = warrantyMonths,
                    OrderItemStatus = od.OrderItemStatus,
                    WarrantyEndDate = od.WarrantyEndDate
                });

                var res = new OrderItemsGroupResponseDTO
                {
                    SellerName = trainer != null ? trainer.Fullname : store?.Name,
                    SellerId = sellerId,
                    SellerRole = trainer != null ? (int)RoleEnum.TRAINER : (int)RoleEnum.STORE,
                    TrackingId = trackingId,
                    OrderItemStatus = orderItemStatusId,
                    TotalAmount = items?.Sum(i => i.Price),
                    Items = items?.ToList()
                };

                return res;
            });

            res.OrderDetailsGrouped = orderDetailsGrouped?.ToList() ?? res.OrderDetailsGrouped;
            OrderResponseDTO response = res;

            return ResponseService<OrderResponseDTO>.OK(response);
        }

        public async Task<GenericResponseDTO<PaginationResponseDTO<OrderResponseDTO>>> GetOrdersPagination(PaginationRequest payload,
            Expression<Func<Orders, bool>> orderExpress,
            Func<OrderItem, bool> orderItemFunc,
            OrderItemStatusEnum? orderItemStatusFilter = null)
        {
            try
            {
                var loginUserId = userServices.GetLoginUserId();
                var role = userServices.GetRole();

                var isGlobalRole = role == (int)RoleEnum.ADMIN || role == (int)RoleEnum.MANAGER || role == (int)RoleEnum.STAFF;
                var paginatedOrders = _unitOfWork.OrderRepository.GetPaginate(
                    filter: orderExpress,
                    orderBy: ob => ob.OrderByDescending(item => item.CreateDate),
                    includeProperties: "OrderItems,OrderItems.IotsDevice,OrderItems.Combo,OrderItems.Lab,OrderItems.Lab.CreatedByNavigation",
                    pageIndex: payload.PageIndex,
                    pageSize: payload.PageSize
                );

                var orderItemIds = paginatedOrders?.Data?.SelectMany(o => o.OrderItems).Select(oi => oi.Id).ToHashSet();

                var reports = _unitOfWork.ReportRepository.Search(r => orderItemIds != null && orderItemIds.Contains(r.OrderItemId)).ToList();

                if (paginatedOrders == null)
                    return ResponseService<PaginationResponseDTO<OrderResponseDTO>>.NotFound("No orders found for this user.");

                var sellerIds = paginatedOrders.Data?.SelectMany(item => item.OrderItems.Select(o => o.SellerId)).Distinct();

                var storeList = _unitOfWork.StoreRepository.Search(
                    item => (sellerIds != null) && sellerIds.Contains(item.OwnerId)
                ).Include(store => store.Owner).ToList();

                var mapper = new MapService<Orders, OrderResponseDTO>();

                // Build to Order Response DTO
                var orderDTOs = paginatedOrders?.Data?.Select(order =>
                {
                    var res = BuildToOrderResponseDTO(mapper, order, orderItemFunc);

                    var orderItems = order.OrderItems
                    .Where(orderItemFunc)
                    .Where(orderItem => orderItemStatusFilter == null || orderItem.OrderItemStatus == (int)orderItemStatusFilter)
                    .ToList();

                    res.TotalPrice = (orderItems?.Sum(o => (decimal)o.Price * o.Quantity) ?? 0) + (res?.ShippingFee ?? 0);
                    res.SellerActualAmount = res.TotalPrice * ((decimal)(APPLICATION_FEE - 10) / 100);

                    var orderDetailsGrouped = orderItems?
                    .GroupBy(od => od.SellerId)
                    .Select(group =>
                    {
                        var od = group?.FirstOrDefault();

                        var sellerId = od?.SellerId;
                        var trackingId = od?.TrackingId;
                        var store = storeList.FirstOrDefault(s => s.OwnerId == sellerId);
                        var trainer = od?.Lab?.CreatedByNavigation;
                        var orderItemStatusId = od.OrderItemStatus;
                        var warrantySerialNumbers = od.SellerId == loginUserId || isGlobalRole ? od?.PhysicalSerialNumbers?.Split("|")?.ToList() : null;
                        var warrantyMonths = od?.IotsDevice?.WarrantyMonth ?? od?.Combo?.WarrantyMonth ?? 0;
                        var actionDate = od.UpdatedDate;

                        var items = group?.Select(od =>
                        {
                            var warrantySerialNumbers = od.SellerId == loginUserId || isGlobalRole ? od?.PhysicalSerialNumbers?.Split("|")?.ToList() : null;
                            var warrantyMonths = od?.IotsDevice?.WarrantyMonth ?? od?.Combo?.WarrantyMonth ?? 0;
                            var report = reports?.FirstOrDefault(r => r.OrderItemId == od.Id);

                            return new OrderItemResponseDTO
                            {
                                OrderItemId = od.Id,
                                ProductId = od?.IosDeviceId ?? od?.LabId ?? od?.ComboId,
                                NameProduct = od?.IotsDevice?.Name ?? od?.Combo?.Name ?? od?.Lab?.Title,
                                ProductType = od.ProductType,
                                ImageUrl = od?.IotsDevice?.ImageUrl ?? od?.Combo?.ImageUrl ?? od?.Lab?.ImageUrl,
                                Quantity = od.Quantity,
                                Price = od.Price,
                                OrderItemStatus = od.OrderItemStatus,
                                WarrantyEndDate = od.WarrantyEndDate,
                                WarrantyMonths = warrantyMonths,
                                PhysicalSerialNumbers = warrantySerialNumbers,
                                UpdatedDate = actionDate,
                                ReportStatus = report?.Status,
                                RefundAmount = report?.RefundAmount ?? 0,
                                RefundQuantity = report?.RefundQuantity ?? 0
                            };
                        });

                        var res = new OrderItemsGroupResponseDTO
                        {
                            StoreId = trainer != null ? trainer.Id : store?.Id,
                            SellerName = trainer != null ? trainer.Fullname : store?.Name,
                            SellerId = sellerId,
                            SellerRole = trainer != null ? (int)RoleEnum.TRAINER : (int)RoleEnum.STORE,
                            TrackingId = trackingId,
                            OrderItemStatus = orderItemStatusId,
                            TotalAmount = group?.Sum(oi => oi.Price * oi.Quantity) ?? 0,
                            Items = items?.ToList()
                        };

                        return res;
                    });

                    res.OrderDetailsGrouped = orderDetailsGrouped?.ToList() ?? res.OrderDetailsGrouped;

                    return res;
                });

                var paginationResponse = new PaginationResponseDTO<OrderResponseDTO>
                {
                    Data = orderDTOs,
                    TotalCount = paginatedOrders?.TotalCount ?? 0,
                    PageIndex = payload.PageIndex,
                    PageSize = payload.PageSize
                };

                return ResponseService<PaginationResponseDTO<OrderResponseDTO>>.OK(paginationResponse);
            }
            catch (Exception ex)
            {
                return ResponseService<PaginationResponseDTO<OrderResponseDTO>>.BadRequest("Cannot get orders. Please try again.");
            }
        }

        public async Task<GenericResponseDTO<PaginationResponseDTO<OrderResponseDTO>>> GetOrdersStoreAndTrainerPagination(PaginationRequest payload,
            Expression<Func<Orders, bool>> orderExpress,
            Func<OrderItem, bool> orderItemFunc,
            OrderItemStatusEnum? orderItemStatusFilter = null)
        {
            try
            {
                var loginUserId = userServices.GetLoginUserId();
                var role = userServices.GetRole();
                var isGlobalRole = role == (int)RoleEnum.ADMIN || role == (int)RoleEnum.MANAGER || role == (int)RoleEnum.STAFF;

                var paginatedOrders = _unitOfWork.OrderRepository.GetPaginate(
                filter: orderExpress,
                orderBy: ob => ob.OrderByDescending(item => item.CreateDate),
                    includeProperties: "OrderItems,OrderItems.IotsDevice,OrderItems.Combo,OrderItems.Lab,OrderItems.Lab.CreatedByNavigation",
                    pageIndex: payload.PageIndex,
                    pageSize: payload.PageSize
                );

                var orderItemIds = paginatedOrders?.Data?.SelectMany(o => o.OrderItems).Select(oi => oi.Id).ToHashSet();

                var reports = _unitOfWork.ReportRepository.Search(r => orderItemIds != null && orderItemIds.Contains(r.OrderItemId)).ToList();

                if (paginatedOrders == null)
                    return ResponseService<PaginationResponseDTO<OrderResponseDTO>>.NotFound("No orders found for this user.");

                var sellerIds = paginatedOrders.Data?.SelectMany(item => item.OrderItems.Select(o => o.SellerId)).Distinct();

                var storeList = _unitOfWork.StoreRepository.Search(
                    item => (sellerIds != null) && sellerIds.Contains(item.OwnerId)
                ).Include(store => store.Owner).ToList();

                var mapper = new MapService<Orders, OrderResponseDTO>();

                // Build to Order Response DTO
                var orderDTOs = paginatedOrders?.Data?.Select(order =>
                {
                    var res = BuildToOrderResponseDTO(mapper, order, orderItemFunc);

                    var orderItems = order.OrderItems
                    .Where(orderItemFunc)
                    .Where(orderItem => orderItemStatusFilter == null || orderItem.OrderItemStatus == (int)orderItemStatusFilter)
                    .ToList();

                    res.TotalPrice = (orderItems?.Sum(o => (decimal)o.Price * o.Quantity) ?? 0) + (res?.ShippingFee ?? 0);
                    res.SellerActualAmount = res.TotalPrice * ((decimal)(100 - APPLICATION_FEE) / 100);

                    var orderDetailsGrouped = orderItems?.GroupBy(od => od.SellerId)
                    .Select(group =>
                    {
                        var od = group?.FirstOrDefault();

                        var sellerId = od?.SellerId;
                        var trackingId = od?.TrackingId;
                        var store = storeList.FirstOrDefault(s => s.OwnerId == sellerId);
                        var trainer = od?.Lab?.CreatedByNavigation;
                        var orderItemStatusId = od.OrderItemStatus;
                        var actionDate = od.UpdatedDate;

                        var items = group?.Select(od =>
                        {
                            var warrantySerialNumbers = (od.SellerId == loginUserId || od.OrderId == loginUserId) || isGlobalRole ? od?.PhysicalSerialNumbers?.Split("|")?.ToList() : null;
                            var warrantyMonths = od?.IotsDevice?.WarrantyMonth ?? od?.Combo?.WarrantyMonth ?? 0;
                            var report = reports?.FirstOrDefault(r => r.OrderItemId == od.Id);

                            return new OrderItemResponseDTO
                            {
                                OrderItemId = od.Id,
                                ProductId = od?.IosDeviceId ?? od?.LabId ?? od?.ComboId,
                                NameProduct = od?.IotsDevice?.Name ?? od?.Combo?.Name ?? od?.Lab?.Title,
                                ProductType = od.ProductType,
                                ImageUrl = od?.IotsDevice?.ImageUrl ?? od?.Combo?.ImageUrl ?? od?.Lab?.ImageUrl,
                                Quantity = od.Quantity,
                                Price = od.Price,
                                OrderItemStatus = od.OrderItemStatus,
                                WarrantyEndDate = od.WarrantyEndDate,
                                WarrantyMonths = warrantyMonths,
                                PhysicalSerialNumbers = warrantySerialNumbers,
                                UpdatedDate = actionDate,
                                ReportStatus = report?.Status,
                                RefundAmount = report?.RefundAmount ?? 0,
                                RefundQuantity = report?.RefundQuantity ?? 0
                            };
                        });

                        var res = new OrderItemsGroupResponseDTO
                        {
                            StoreId = trainer != null ? trainer.Id : store?.Id,
                            SellerName = trainer != null ? trainer.Fullname : store?.Name,
                            SellerId = sellerId,
                            SellerRole = trainer != null ? (int)RoleEnum.TRAINER : (int)RoleEnum.STORE,
                            TrackingId = trackingId,
                            OrderItemStatus = orderItemStatusId,
                            TotalAmount = group.Sum(oi => oi.Price * oi.Quantity),
                            Items = items?.ToList()
                        };

                        return res;
                    });

                    res.OrderDetailsGrouped = orderDetailsGrouped?.ToList() ?? res.OrderDetailsGrouped;

                    return res;
                });

                var paginationResponse = new PaginationResponseDTO<OrderResponseDTO>
                {
                    Data = orderDTOs,
                    TotalCount = paginatedOrders?.TotalCount ?? 0,
                    PageIndex = payload.PageIndex,
                    PageSize = payload.PageSize
                };

                return ResponseService<PaginationResponseDTO<OrderResponseDTO>>.OK(paginationResponse);
            }
            catch (Exception ex)
            {
                return ResponseService<PaginationResponseDTO<OrderResponseDTO>>.BadRequest("Cannot get orders. Please try again.");
            }
        }

        public async Task<GenericResponseDTO<PaginationResponseDTO<OrderResponseDTO>>> GetOrdersByUserPagination(
            PaginationRequest payload,
            OrderItemStatusEnum? orderItemStatusFilter = null)
        {
            var loginUserId = userServices.GetLoginUserId();

            if (loginUserId == null || !await userServices.CheckLoginUserRole(RoleEnum.CUSTOMER))
                return ResponseService<PaginationResponseDTO<OrderResponseDTO>>.Unauthorize("You don't have permission to access");

            Expression<Func<Orders, bool>> func = item => item.OrderBy == (int)loginUserId && item.OrderItems.Any(o => orderItemStatusFilter == null || o.OrderItemStatus == (int)orderItemStatusFilter)
                                                   && (item.ApplicationSerialNumber.Contains(payload.SearchKeyword));

            var pagination = await GetOrdersPagination(
                payload,
                func,
                orderItem => true,
                orderItemStatusFilter
            );

            return pagination;
        }

        public async Task<GenericResponseDTO<PaginationResponseDTO<OrderResponseDTO>>> GetOrderByStoreOrTrainerPagination(
            PaginationRequest payload,
            OrderItemStatusEnum? orderItemStatusFilter = null)
        {
            var loginUserId = userServices.GetLoginUserId();

            var role = userServices.GetRole();

            var storeTrainerRoles = new List<int> { (int)RoleEnum.STORE, (int)RoleEnum.TRAINER };

            if (loginUserId == null || role == null || !storeTrainerRoles.Contains((int)role))
                return ResponseService<PaginationResponseDTO<OrderResponseDTO>>.Unauthorize("You don't have permission to access");

            Expression<Func<Orders, bool>> func =
                item => item.OrderItems.Any(item => item.SellerId == loginUserId && (orderItemStatusFilter == null || (int)orderItemStatusFilter == item.OrderItemStatus))
                        && (item.ApplicationSerialNumber.Contains(payload.SearchKeyword));

            var pagination = await GetOrdersStoreAndTrainerPagination(
                payload,
                func,
                oi => oi.SellerId == loginUserId,
                orderItemStatusFilter
            );

            return pagination;
        }

        public async Task<GenericResponseDTO<PaginationResponseDTO<OrderResponseDTO>>> GetAdminOrdersPagination(
            PaginationRequest payload,
            OrderItemStatusEnum? orderItemStatusFilter = null)
        {
            var loginUserId = userServices.GetLoginUserId();

            if (loginUserId == null || (!await userServices.CheckLoginUserRole(RoleEnum.MANAGER) && !await userServices.CheckLoginUserRole(RoleEnum.ADMIN)))
                return ResponseService<PaginationResponseDTO<OrderResponseDTO>>.Unauthorize("You don't have permission to access");

            Expression<Func<Orders, bool>> func =
                item => (orderItemStatusFilter == null || item.OrderItems.Any(o => o.OrderItemStatus == (int)orderItemStatusFilter))
                        && (item.ApplicationSerialNumber.Contains(payload.SearchKeyword));

            var pagination = await GetOrdersPagination(
                payload,
                func,
                oi => true,
                orderItemStatusFilter
            );

            return pagination;
        }

        public async Task<GenericResponseDTO<PaginationResponseDTO<OrderResponseToStoreDTO>>> GetOrderByStoreIdHasStatusPending(int? filterOrderId, PaginationRequest payload)
        {
            try
            {
                var loginUser = userServices.GetLoginUser();

                if (loginUser == null || !await userServices.CheckLoginUserRole(RoleEnum.STORE))
                    return ResponseService<PaginationResponseDTO<OrderResponseToStoreDTO>>.Unauthorize("You don't have permission to access");

                var storeId = loginUser.Id;

                var orders = await _unitOfWork.OrderRepository.GetOrdersByStoreIdAsync(storeId);

                if (orders == null || !orders.Any())
                    return ResponseService<PaginationResponseDTO<OrderResponseToStoreDTO>>.NotFound("No orders found for this store.");

                var filteredOrders = orders.Where(order =>
                    (filterOrderId == null || order.Id == filterOrderId)
                );

                int totalOrders = filteredOrders.Count();

                var paginatedOrders = filteredOrders
                    .OrderByDescending(order => order.CreateDate)
                    .Skip((payload.PageIndex - 1) * payload.PageSize)
                    .Take(payload.PageSize)
                    .ToList();

                // Chuyển đổi danh sách Order thành OrderResponseToStoreDTO
                var orderDTOs = paginatedOrders.Select(order => new OrderResponseToStoreDTO
                {
                    ApplicationSerialNumber = order.ApplicationSerialNumber,
                    TotalPrice = order.TotalPrice,
                    Address = order.Address,
                    ContactNumber = order.ContactNumber,
                    Notes = order.Notes,
                    CreateDate = order.CreateDate,
                    UpdatedDate = order.UpdatedDate,
                    OrderStatusId = order.OrderStatusId,
                    OrderDetails = order.OrderItems
                        .Where(oi => oi.SellerId == storeId && oi.OrderItemStatus == 1)
                        .Select(oi => new OrderIstemResponseToStoreDTO
                        {
                            Id = oi.Id,
                            OrderId = oi.OrderId,
                            IosDeviceId = oi.IosDeviceId,
                            IosDeviceName = oi.IosDeviceId != null ? oi.IotsDevice.Name : null,
                            ComboId = oi.ComboId,
                            ComboName = oi.ComboId != null ? oi.Combo.Name : null,
                            SellerId = oi.SellerId,
                            ProductType = oi.ProductType,
                            Quantity = oi.Quantity,
                            Price = oi.Price,
                            WarrantyEndDate = oi.WarrantyEndDate,
                            OrderItemStatus = oi.OrderItemStatus
                        }).ToList()
                }).ToList();

                var paginationResponse = new PaginationResponseDTO<OrderResponseToStoreDTO>
                {
                    Data = orderDTOs,
                    TotalCount = totalOrders,
                    PageIndex = payload.PageIndex,
                    PageSize = payload.PageSize
                };

                return ResponseService<PaginationResponseDTO<OrderResponseToStoreDTO>>.OK(paginationResponse);
            }
            catch (Exception ex)
            {
                return ResponseService<PaginationResponseDTO<OrderResponseToStoreDTO>>.BadRequest("Cannot get orders. Please try again.");
            }
        }

        public async Task<GenericResponseDTO<OrderResponseDTO>> GetOrdersDetailsByOrderId(int orderId,
            OrderItemStatusEnum? orderItemStatusFilter = null)
        {
            var loginUserId = userServices.GetLoginUserId();

            var role = userServices.GetRole();

            if (loginUserId == null)
                return ResponseService<OrderResponseDTO>.Unauthorize("You don't have permission to access");

            OrderResponseDTO? result = null;

            if (role == (int)RoleEnum.CUSTOMER)
            {
                var res = await GetOrderIncludedGroupOrderItems(o => o.Id == orderId && o.OrderBy == loginUserId,
                    oi => true,
                    orderItemStatusFilter);

                result = res?.Data;
            }
            else if (role == (int)RoleEnum.STORE || role == (int)RoleEnum.TRAINER)
            {
                var res = await GetOrderIncludedGroupOrderItems(order => order.Id == orderId && order.OrderItems.Any(orderItem => orderItem.SellerId == loginUserId),
                    item => item.SellerId == loginUserId,
                    orderItemStatusFilter);

                result = res?.Data;
            }
            else if (role == (int)RoleEnum.ADMIN || role == (int)RoleEnum.STAFF)
            {
                var res = await GetOrderIncludedGroupOrderItems(order => orderId == order.Id,
                    item => true,
                    orderItemStatusFilter);

                result = res?.Data;
            }

            if (result == null)
                return ResponseService<OrderResponseDTO>.NotFound("Order cannot be found. Please try again");

            return ResponseService<OrderResponseDTO>.OK(result);
        }

        public async Task<ResponseDTO> UpdateSellerGroupOrderItemStatus(int orderId, OrderItemStatusEnum status)
        {
            try
            {
                var loginUserId = userServices.GetLoginUserId();

                if (loginUserId == null)
                    return ResponseService<List<OrderResponseToStoreDTO>>.Unauthorize(ExceptionMessage.INVALID_PERMISSION);

                if (orderId != 0)
                {
                    var orderItemsToUpdate = _unitOfWork.OrderDetailRepository
                        .Search(item => item.SellerId == loginUserId && item.OrderId == orderId).ToList();

                    if (!orderItemsToUpdate.Any())
                        return ResponseService<List<OrderResponseToStoreDTO>>.NotFound("No order items found for this store.");

                    foreach (var item in orderItemsToUpdate)
                    {
                        item.OrderItemStatus = (int)status;
                        item.UpdatedDate = DateTime.Now;
                    }

                    await _unitOfWork.OrderDetailRepository.UpdateAsync(orderItemsToUpdate);

                    var trackingIds = orderItemsToUpdate.Select(item => item.TrackingId).FirstOrDefault();
                    var orderItemStatus = orderItemsToUpdate.Select(item => item.OrderItemStatus).FirstOrDefault();
                    return ResponseService<object>.OK(new
                    {
                        OrderId = orderId,
                        TrackingId = trackingIds,
                        OrderItemStatus = orderItemStatus
                    });
                }
                return ResponseService<object>.BadRequest("Invalid orderId.");
            }
            catch (Exception ex)
            {
                return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Cannot get orders. Please try again.");
            }
        }

        public async Task<ResponseDTO> UpdateOrderDetailToPacking(int updateOrderId,
            CreateOrderWarrantyInfo request)
        {
            try
            {
                var loginUserId = userServices.GetLoginUserId();

                var role = userServices.GetRole();

                if (loginUserId == null || role == (int)RoleEnum.CUSTOMER)
                    return ResponseService<List<OrderResponseToStoreDTO>>.Unauthorize("You don't have permission to access");

                var isNotAllPending = _unitOfWork.OrderDetailRepository
                        .Search(item => item.SellerId == loginUserId && item.OrderId == updateOrderId)
                        .Any(item => item.OrderItemStatus != (int)OrderItemStatusEnum.PENDING);

                if (isNotAllPending)
                    return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Your Order Status must be pending before packing. Please try again");

                var response = await UpdateSellerGroupOrderItemStatus(updateOrderId, OrderItemStatusEnum.PACKING);

                if (request != null)
                {
                    var orderItemIds = request?.OrderProductInfo?.Select(item => item.OrderItemId).ToList();

                    var orderItemsToUpdate = _unitOfWork.OrderDetailRepository
                        .Search(item => (orderItemIds != null) && item.SellerId == loginUserId && item.OrderId == updateOrderId && orderItemIds.Any(o => o == item.Id)).ToList();

                    foreach (var item in orderItemsToUpdate)
                    {
                        var serialNumbers = request?.OrderProductInfo?.FirstOrDefault(p => p.OrderItemId == item.Id);

                        if (serialNumbers?.PhysicalSerialNumber != null)
                            item.PhysicalSerialNumbers = String.Join("|", serialNumbers.PhysicalSerialNumber);
                    }

                    await _unitOfWork.OrderDetailRepository.UpdateAsync(orderItemsToUpdate);
                }

                return response;
            }
            catch (Exception ex)
            {
                return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Cannot get orders. Please try again.");
            }
        }

        public async Task<ResponseDTO> UpdateOrderDetailToDelivering(int updateOrderId)
        {
            try
            {
                var loginUserId = userServices.GetLoginUserId();

                var role = userServices.GetRole();

                if (loginUserId == null || role == (int)RoleEnum.CUSTOMER)
                    return ResponseService<List<OrderResponseToStoreDTO>>.Unauthorize("You don't have permission to access");

                var isNotAllPending = _unitOfWork.OrderDetailRepository
                        .Search(item => item.SellerId == loginUserId && item.OrderId == updateOrderId)
                        .Any(item => item.OrderItemStatus != (int)OrderItemStatusEnum.PACKING);

                if (isNotAllPending)
                    return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Your Order Status must be packing before delivering. Please try again");

                var response = await UpdateSellerGroupOrderItemStatus(updateOrderId, OrderItemStatusEnum.DELEVERING);

                return response;
            }
            catch (Exception ex)
            {
                return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Cannot get orders. Please try again.");
            }
        }

        public async Task<ResponseDTO> UpdateOrderDetailToPendingToFeedback(int updateOrderId, int sellerId)
        {
            try
            {
                var loginUserId = userServices.GetLoginUserId();

                var role = userServices.GetRole();

                if (role != (int)RoleEnum.CUSTOMER && role != (int)RoleEnum.MANAGER && role != (int)RoleEnum.ADMIN)
                    return ResponseService<List<OrderResponseToStoreDTO>>.Unauthorize("You don't have permission to access");

                var query = _unitOfWork.OrderDetailRepository
                        .Search(item => item.SellerId == sellerId && item.OrderId == updateOrderId)
                        .Include(item => item.IotsDevice).Include(item => item.Combo);

                var isNotAllPending = query.Any(item => item.OrderItemStatus != (int)OrderItemStatusEnum.DELEVERING);

                if (isNotAllPending)
                    return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Your Order Status must be delivering before delivered. Please try again");

                var orderItems = query.ToList();

                foreach (var item in orderItems)
                {
                    var warrantyMonth = item?.IotsDevice?.WarrantyMonth ?? item?.Combo?.WarrantyMonth ?? 0;

                    item.OrderItemStatus = (int)OrderItemStatusEnum.PENDING_TO_FEEDBACK;
                    item.UpdatedDate = DateTime.Now;
                    item.WarrantyEndDate = DateTime.Now.AddMonths(warrantyMonth);
                }

                await _unitOfWork.OrderDetailRepository.UpdateAsync(orderItems);

                var trackingIds = orderItems.Select(item => item.TrackingId).FirstOrDefault();

                _ = activityLogService.CreateActivityLog($"Change status of order {updateOrderId} of seller id {sellerId} to pending to feedback");

                return ResponseService<object>.OK(new
                {
                    OrderId = updateOrderId,
                    TrackingId = trackingIds,
                    OrderItemStatus = (int)OrderItemStatusEnum.PENDING_TO_FEEDBACK
                });
            }
            catch (Exception ex)
            {
                return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Cannot get orders. Please try again.");
            }
        }

        public async Task<ResponseDTO> UpdateOrderDetailToSuccess(int orderId)
        {
            try
            {
                var loginUserId = userServices.GetLoginUserId();

                var role = userServices.GetRole();

                if (loginUserId == null || role == (int)RoleEnum.CUSTOMER)
                    return ResponseService<List<OrderResponseToStoreDTO>>.Unauthorize(ExceptionMessage.INVALID_PERMISSION);

                var isNotAllPendingToFeedback = _unitOfWork.OrderDetailRepository
                        .Search(item => item.SellerId == loginUserId && item.OrderId == orderId)
                        .Any(item => item.OrderItemStatus != (int)OrderItemStatusEnum.PENDING_TO_FEEDBACK);

                if (isNotAllPendingToFeedback)
                    return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Your Order Status must be delivered before completed. Please try again");

                var order = _unitOfWork.OrderRepository.GetById(orderId);

                var orderItems = _unitOfWork.OrderDetailRepository.Search(
                    item => item.OrderId == orderId && item.SellerId == loginUserId
                ).ToList();

                var actionDate = orderItems?.FirstOrDefault()?.UpdatedDate;

                var days = (DateTime.Now - actionDate)?.Days;

                if (days < AUTO_COMPLETE_DAYS)
                {
                    return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest($"You are not allowed to change its status to Completed until {order.CreateDate.AddDays(AUTO_COMPLETE_DAYS).Date}. Please wait");
                }

                var response = await UpdateSellerGroupOrderItemStatus(orderId, OrderItemStatusEnum.SUCCESS_ORDER);
                decimal appRevenue = 0;

                if (response.IsSuccess)
                {
                    decimal totalReceivedGolds = orderItems?.Sum(item =>
                    {
                        var amount = (decimal)(item.Price * item.Quantity);
                        var fee = amount * ((decimal)APPLICATION_FEE / 100);

                        appRevenue += fee / 1000;

                        return (amount - fee) / 1000;
                    }) ?? 0;

                    var wallet = (await walletService.GetWalletByUserId((int)loginUserId)).Data;

                    wallet.Ballance += totalReceivedGolds;

                    Transaction trans = new Transaction
                    {
                        Amount = totalReceivedGolds,
                        CreatedDate = DateTime.UtcNow.AddHours(7),
                        CurrentBallance = wallet.Ballance,
                        Description = $"You have received {totalReceivedGolds} gold for Success Order {order.ApplicationSerialNumber}",
                        Status = "Success",
                        TransactionType = $"Order {order.ApplicationSerialNumber}",
                        UserId = (int)loginUserId
                    };

                    Transaction appTrans = new Transaction
                    {
                        Amount = appRevenue,
                        CreatedDate = DateTime.UtcNow.AddHours(7),
                        CurrentBallance = 0,
                        Description = $"You have received {appRevenue} gold for Success Order {order.ApplicationSerialNumber}",
                        Status = "Success",
                        TransactionType = $"Order {order.ApplicationSerialNumber}",
                        UserId = AdminConst.ADMIN_ID,
                        IsApplication = 1
                    };

                    _unitOfWork.WalletRepository.Update(wallet);

                    _ = _unitOfWork.TransactionRepository.CreateAsync(new List<Transaction> { trans, appTrans });
                }

                return response;
            }
            catch (Exception ex)
            {
                return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Cannot get orders. Please try again.");
            }
        }

        public async Task<ResponseDTO> UpdateOnlinePaymentOrderDetailToCancel(int orderId, CreateRefundRequestDTO request)
        {
            try
            {
                var loginUserId = (int)userServices.GetLoginUserId();

                var role = userServices.GetRole();

                if (role != (int)RoleEnum.CUSTOMER)
                    return ResponseService<List<OrderResponseToStoreDTO>>.Unauthorize(ExceptionMessage.INVALID_PERMISSION);

                var order = _unitOfWork.OrderRepository.GetById(orderId);

                if (order.OrderStatusId != (int)OrderStatusEnum.SUCCESS_TO_ORDER)
                {
                    return ResponseService<object>.BadRequest("The Order is not VnPay payment Orders. Please try again");
                }

                var isNotAllPending = _unitOfWork.OrderDetailRepository
                        .Search(item => item.OrderId == orderId)
                        .Any(item => item.OrderItemStatus != (int)OrderItemStatusEnum.PENDING);

                if (isNotAllPending)
                    return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Some orders was packing or delivering by store. You cannot cancel the order now");

                if (order.OrderBy != loginUserId)
                    return ResponseService<object>.Unauthorize(ExceptionMessage.INVALID_PERMISSION);

                var numberOfCancelled = _unitOfWork.OrderRepository
                    .Search(item => item.OrderStatusId == (int)OrderStatusEnum.CANCELLED
                    && item.OrderBy == loginUserId
                    && item.CreateDate.Date == DateTime.Now.Date).Count();

                if (numberOfCancelled > MAX_CANCELLED_PER_DAYS)
                    return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest($"You cannot cancel more than {MAX_CANCELLED_PER_DAYS} orders per day");

                var orderItems = _unitOfWork.OrderDetailRepository.Search(
                    item => item.OrderId == orderId
                ).Include(item => item.IotsDevice)
                .Include(item => item.Combo).ToList();

                if (order == null)
                    return ResponseService<List<OrderResponseToStoreDTO>>.NotFound("Your Order Status must be pending before cancelled.");

                var saveOrderItems = orderItems?.Select(
                    item =>
                    {
                        item.OrderItemStatus = (int)OrderItemStatusEnum.CANCELLED;
                        item.UpdatedDate = DateTime.Now;

                        return item;
                    }
                ).ToList();

                order.OrderStatusId = (int)OrderStatusEnum.CANCELLED;
                order.UpdatedDate = DateTime.Now;
                order.UpdatedBy = loginUserId;

                order = _unitOfWork.OrderRepository.Update(order);

                var deviceList = orderItems?
                    .Where(item => item.IotsDevice != null)?
                    .Select(item =>
                    {
                        item.IotsDevice.Quantity += item.Quantity;

                        return item.IotsDevice;
                    })?.ToList();

                var comboList = orderItems?
                    .Where(item => item.Combo != null)?
                    .Select(item =>
                    {
                        item.Combo.Quantity += item.Quantity;

                        return item.Combo;
                    })?.ToList();

                if (deviceList != null)
                    await _unitOfWork.IotsDeviceRepository.UpdateAsync(deviceList);

                if (comboList != null)
                    await _unitOfWork.ComboRepository.UpdateAsync(comboList);

                await _unitOfWork.OrderDetailRepository.UpdateAsync(saveOrderItems);

                var totalRefunded = order.TotalPrice - (order.ShippingFee ?? 0);

                var refundRequest = new RefundRequest
                {
                    AccountName = request.AccountName,
                    AccountNumber = request.AccountNumber,
                    BankName = request.BankName,
                    ContactNumber = request.ContactNumber,
                    ActionBy = loginUserId,
                    ActionDate = DateTime.Now,
                    CreatedBy = loginUserId,
                    CreatedDate = DateTime.Now,
                    Amount = totalRefunded,
                    OrderId = orderId
                };

                _ = _unitOfWork.RefundRequestRepository.Create(refundRequest);

                return ResponseService<object>.OK(new
                {
                    OrderId = orderId,
                    OrderItemStatus = (int)OrderItemStatusEnum.CANCELLED
                });
            }
            catch (Exception ex)
            {
                return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Cannot cancel orders. Please try again.");
            }
        }

        public async Task<ResponseDTO> UpdateCashpaymentOrderDetailToCancel(int orderId)
        {
            try
            {
                var loginUserId = (int)userServices.GetLoginUserId();

                var role = userServices.GetRole();

                if (role != (int)RoleEnum.CUSTOMER)
                    return ResponseService<List<OrderResponseToStoreDTO>>.Unauthorize(ExceptionMessage.INVALID_PERMISSION);

                var order = _unitOfWork.OrderRepository.GetById(orderId);

                if (order.OrderStatusId != (int)OrderStatusEnum.CASH_PAYMENT)
                {
                    return ResponseService<object>.BadRequest("The Order is not Cash Payment Order. Please try again");
                }

                var isNotAllPending = _unitOfWork.OrderDetailRepository
                        .Search(item => item.OrderId == orderId)
                        .Any(item => item.OrderItemStatus != (int)OrderItemStatusEnum.PENDING);

                if (isNotAllPending)
                    return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Some orders was packing or delivering by store. You cannot cancel the order now");

                if (order.OrderBy != loginUserId)
                    return ResponseService<object>.Unauthorize(ExceptionMessage.INVALID_PERMISSION);

                var numberOfCancelled = _unitOfWork.OrderRepository
                    .Search(item => item.OrderStatusId == (int)OrderStatusEnum.CANCELLED
                    && item.OrderBy == loginUserId
                    && item.CreateDate.Date == DateTime.Now.Date).Count();

                if (numberOfCancelled > MAX_CANCELLED_PER_DAYS)
                    return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest($"You cannot cancel more than {MAX_CANCELLED_PER_DAYS} orders per day");

                var orderItems = _unitOfWork.OrderDetailRepository.Search(
                    item => item.OrderId == orderId
                ).Include(item => item.IotsDevice)
                .Include(item => item.Combo).ToList();

                if (order == null)
                    return ResponseService<List<OrderResponseToStoreDTO>>.NotFound("Your Order Status must be pending before cancelled.");

                var saveOrderItems = orderItems?.Select(
                    item =>
                    {
                        item.OrderItemStatus = (int)OrderItemStatusEnum.CANCELLED;
                        item.UpdatedDate = DateTime.Now;

                        return item;
                    }
                ).ToList();

                order.OrderStatusId = (int)OrderStatusEnum.CANCELLED;
                order.UpdatedDate = DateTime.Now;
                order.UpdatedBy = loginUserId;

                order = _unitOfWork.OrderRepository.Update(order);

                var deviceList = orderItems?
                    .Where(item => item.IotsDevice != null)?
                    .Select(item =>
                    {
                        item.IotsDevice.Quantity += item.Quantity;

                        return item.IotsDevice;
                    })?.ToList();

                var comboList = orderItems?
                    .Where(item => item.Combo != null)?
                    .Select(item =>
                    {
                        item.Combo.Quantity += item.Quantity;

                        return item.Combo;
                    })?.ToList();

                if (deviceList != null)
                    await _unitOfWork.IotsDeviceRepository.UpdateAsync(deviceList);

                if (comboList != null)
                    await _unitOfWork.ComboRepository.UpdateAsync(comboList);

                await _unitOfWork.OrderDetailRepository.UpdateAsync(saveOrderItems);

                return ResponseService<object>.OK(new
                {
                    OrderId = orderId,
                    OrderItemStatus = (int)OrderItemStatusEnum.CANCELLED
                });
            }
            catch (Exception ex)
            {
                return ResponseService<List<OrderResponseToStoreDTO>>.BadRequest("Cannot cancel orders. Please try again.");
            }
        }

        public async Task<GenericResponseDTO<OrderReturnPaymentVNPayDTO>> CreateOrderByMobile(int? id, OrderRequestDTO payload)
        {
            try
            {
                var loginUser = userServices.GetLoginUser();

                if (loginUser == null || !await userServices.CheckLoginUserRole(RoleEnum.CUSTOMER))
                    return ResponseService<OrderReturnPaymentVNPayDTO>.Unauthorize("You don't have permission to access");

                var loginUserId = loginUser.Id;

                var address = payload.Address;
                var contactPhone = payload.ContactNumber;
                var notes = payload.Notes;
                if (address.Length > 70)
                {
                    throw new ArgumentException("Address cannot exceed 70 characters.");
                }

                if (!Regex.IsMatch(contactPhone, @"^0\d{9}$"))
                {
                    throw new ArgumentException("Contact phone must be a 10-digit number starting with 0.");
                }

                if (notes.Length > 100)
                {
                    throw new ArgumentException("Notes cannot exceed 100 characters.");
                }

                var provinces = await _ghtkService.SyncProvincesAsync();
                var province = provinces.FirstOrDefault(p => p.Id == payload.ProvinceId);
                if (province == null)
                    return ResponseService<OrderReturnPaymentVNPayDTO>.BadRequest("Invalid Province ID");

                var districts = await _ghtkService.SyncDistrictsAsync(payload.ProvinceId);
                var district = districts.FirstOrDefault(d => d.Id == payload.DistrictId);
                if (district == null)
                    return ResponseService<OrderReturnPaymentVNPayDTO>.BadRequest("Invalid District ID");

                var wards = await _ghtkService.SyncWardsAsync(payload.DistrictId);
                var ward = wards.FirstOrDefault(w => w.Id == payload.WardId);
                if (ward == null)
                    return ResponseService<OrderReturnPaymentVNPayDTO>.BadRequest("Invalid Ward ID");

                var addresses = await _ghtkService.SyncAddressAsync(payload.WardId);
                var addressid = addresses.FirstOrDefault(w => w.Id == payload.AddressId);
                if (addressid == null)
                    return ResponseService<OrderReturnPaymentVNPayDTO>.BadRequest("Invalid Address ID");

                // Get the list of selected products in the cart
                var selectedItems = await _unitOfWork.CartRepository
                    .GetQueryable((int)loginUserId)
                    .Where(item => item.CreatedBy == loginUserId && item.IsSelected)
                    .Include(item => item.IosDeviceNavigation)
                    .Include(item => item.ComboNavigation)
                    .Include(item => item.LabNavigation)
                    .ToListAsync();

                if (selectedItems == null || !selectedItems.Any())
                {
                    return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                    {
                        IsSuccess = false,
                        Message = "The cart is empty or no products have been selected.",
                        Data = null
                    };
                }

                var totalPrice = selectedItems.Sum(item =>
                    (((item.IosDeviceNavigation?.Price ?? 0m) * item.Quantity) + ((item.ComboNavigation?.Price ?? 0m) * item.Quantity)
                    + ((item.LabNavigation?.Price ?? 0m) * item.Quantity)
                    )
                );

                if (totalPrice <= 0)
                {
                    return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                    {
                        IsSuccess = false,
                        Message = "Invalid order value.",
                        Data = null
                    };
                }

                foreach (var item in selectedItems)
                {
                    if (item.IosDeviceNavigation != null)
                    {
                        var device = await _unitOfWork.IotsDeviceRepository.GetByIdAsync(item.IosDeviceNavigation.Id);
                        if (device == null || device.Quantity < item.Quantity)
                        {
                            return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                            {
                                IsSuccess = false,
                                Message = $"Product {device?.Name ?? "Unknown"} is out of stock.",
                                Data = null
                            };
                        }
                    }
                    if (item.ComboNavigation != null)
                    {
                        var combo = await _unitOfWork.ComboRepository.GetByIdAsync(item.ComboNavigation.Id);
                        if (combo == null || combo.Quantity < item.Quantity)
                        {
                            return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                            {
                                IsSuccess = false,
                                Message = $"Combo {combo?.Name ?? "Unknown"} is out of stock.",
                                Data = null
                            };
                        }
                    }
                }

                var shippingFees = await _ghtkService.GetShippingFeeAsync(new ShippingFeeRequest
                {
                    ProvinceId = payload.ProvinceId,
                    DistrictId = payload.DistrictId,
                    WardId = payload.WardId,
                    AddressId = payload.AddressId,
                    Address = payload.Address,
                    deliver_option = payload.deliver_option
                });

                var totalShippingFee = shippingFees.FirstOrDefault(f => f.ShopOwnerId == -1)?.Fee ?? 0m;

                var finalTotalPrice = totalPrice + totalShippingFee;

                var orderInfo = new OrderInfoByMobile
                {
                    UserId = loginUserId,
                    Address = address,
                    ContactNumber = contactPhone,
                    Notes = notes,
                    ProvinceId = province.Id,
                    DistrictId = district.Id,
                    WardId = ward.Id,
                    AddressId = addressid.Id,
                    deliver_option = payload.deliver_option
                };

                string vnp_ReturnUrl = "https://fe-capstone-io-ts.vercel.app/checkout-process-order-by-mobile";
                string vnp_Url = "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html";
                string vnp_TmnCode = "PJLU0FHO";
                string vnp_HashSecret = "4RY7BQN7ED5YFS7YR4TS3YONAJPGYYFL";

                // Convert orderInfo to JSON
                string orderInfoJson = JsonConvert.SerializeObject(orderInfo);
                Console.WriteLine("OrderInfo JSON: " + orderInfoJson);

                // Encode to Base64
                string encryptedOrderInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes(orderInfoJson));
                Console.WriteLine("Encoded OrderInfo (Base64): " + encryptedOrderInfo);
                if (orderInfoJson.Length > 255) // VNPay may have a 255-character limit
                {
                    Console.WriteLine("OrderInfo is too long, needs to be shortened!");
                }
                // Decode back to verify
                try
                {
                    string decodedOrderInfo = Encoding.UTF8.GetString(Convert.FromBase64String(encryptedOrderInfo));
                    Console.WriteLine("Decoded OrderInfo: " + decodedOrderInfo);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error when decoding Base64: " + ex.Message);
                }

                if (string.IsNullOrEmpty(vnp_TmnCode) || string.IsNullOrEmpty(vnp_HashSecret))
                {
                    throw new Exception("Merchant code or secret key is missing.");
                }

                // Generate transaction reference number
                var vnp_TxnRef = $"{loginUserId}{DateTime.Now:yyyyMMddHHmmssfff}{new Random().Next(1000, 9999)}";

                long vnp_Amount = (long)(finalTotalPrice * 100);
                if (finalTotalPrice <= 0)
                {
                    return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                    {
                        IsSuccess = false,
                        Message = "Invalid order value.",
                        Data = null
                    };
                }

                var vnpay = new VnPayLibrary();
                vnpay.AddRequestData("vnp_Version", "2.1.0");
                vnpay.AddRequestData("vnp_Command", "pay");
                vnpay.AddRequestData("vnp_TmnCode", vnp_TmnCode);
                vnpay.AddRequestData("vnp_Amount", vnp_Amount.ToString());
                TimeZoneInfo vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                DateTime vietnamTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
                vnpay.AddRequestData("vnp_CreateDate", vietnamTime.AddMinutes(-20).ToString("yyyyMMddHHmmss"));
                vnpay.AddRequestData("vnp_CurrCode", "VND");
                vnpay.AddRequestData("vnp_IpAddr", Utils.GetIpAddress());
                vnpay.AddRequestData("vnp_Locale", "vn");
                vnpay.AddRequestData("vnp_OrderInfo", encryptedOrderInfo);
                vnpay.AddRequestData("vnp_OrderType", "order");
                vnpay.AddRequestData("vnp_ReturnUrl", vnp_ReturnUrl);
                vnpay.AddRequestData("vnp_TxnRef", vnp_TxnRef);
                vnpay.AddRequestData("vnp_ExpireDate", vietnamTime.AddMinutes(5).ToString("yyyyMMddHHmmss"));
                string paymentUrl = vnpay.CreateRequestUrl(vnp_Url, vnp_HashSecret);

                return new GenericResponseDTO<OrderReturnPaymentVNPayDTO>
                {
                    IsSuccess = true,
                    Message = "Order created successfully, please proceed with payment.",
                    Data = new OrderReturnPaymentVNPayDTO
                    {
                        PaymentUrl = paymentUrl
                    }
                };
            }
            catch (Exception ex)
            {
                throw new Exception($"An error occurred during payment: {ex.Message}");
            }
        }
    }
}
