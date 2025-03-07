﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CaptoneProject_IOTS_BOs.Constant
{
    public enum OrderStatusEnum
    {
        SUCCESS_TO_ORDER = 1,
        REJECTED_BY_CUSTOMER = 2
    }

    public enum OrderItemStatusEnum
    {
        PENDING = 1,
        PENDING_TO_FEEDBACK = 2,
        COMPLETED = 3,
        CLOSED = 4
    }
}
