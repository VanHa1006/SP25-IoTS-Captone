﻿using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CaptoneProject_IOTS_BOs.Constant
{
    public static class ExceptionMessage
    {
        public static string SESSION_EXPIRED = "Your session is expired. Please login";
        public static string INVALID_LOGIN = "Please login to access";
        public static string USER_EXIST_EXCEPTION = "User already existing. Please try again";
        public static string EMAIL_DOESNT_EXIST = "Email doesn't exist";
        public static string USER_DOESNT_EXIST = "User doesn't exist";
        public static string INVALID_PERMISSION = "You don't have permission to access";
        public static string INVALID_STORE_ROLE = "To do this action. The role of user must be Store Owner";
        public static string INVALID_TRAINER_ROLE = "To do this action. The role of user must be Trainer";

        public static string INVALID_STAFF_MANAGER_ROLE = "To do this action. The role of user must be Staff or Manager";

        public static string USER_REQUEST_NOT_FOUND = "User request doesn't exist";

        public static string USER_EMAIL_INVALID = "User email is invalid";

        public static string EMAIL_ALREADY_VERIFIED = "The email is already existing";

        public static string EXPIRED_OTP = "The otp was Expired";

        public static string INCORRECT_OTP = "The entered otp was incorrect. Please try again";

        public static string LOGIN_INACTIVE_ACCOUNT = "Your account was inactive. Please contact with admin to activate";

        public static string INCORRECT_PASSWORD = "Your password is incorrect. Please try again";

        public static string MATERIAL_CATEGORY_NOTFOUND = "Cannot find material category";

        public static string MATERIAL_GROUP_CATEGORY_NOTFOUND = "Cannot find material group category";

        public static string MATERIAL_NOTFOUND = "Cannot find material";

        public static string LAB_NOTFOUND = "The playlist cannot be found. Please try again";
        public static string DEVICE_NOTFOUND = "Iot Device cannot be found. Please try again";
        public static string STORE_NOTFOUND = "Store cannot be found. Please try again";
        public static string INSUFFICIENT_WALLET = "Wallet balance is insufficient. Please add funds to your wallet.";
        public static string REGISTER_PACKAGE_TOO_SOON = "It's too early to buy a new package. You can only purchase it at most 5 days before the payment date.";
        public static string BLOG_CATEGORY_NOTFOUND = "Cannot find blog category";
        public static string PRODUCT_CANNOT_BE_FOUND = "The product cannot be found. Please try again";
        public static string Insufficient_product_quantity = "Insufficient product quantity. Please try again";

        public static string NOTFOUND_PLAYLIST = "Playlist cannot be found. Please try again";
    }
}
