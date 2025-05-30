﻿using CaptoneProject_IOTS_BOs;
using CaptoneProject_IOTS_BOs.DTO.MessageDTO;
using CaptoneProject_IOTS_BOs.DTO.RabbitMQDTO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CaptoneProject_IOTS_Service.Services.Interface
{
    public interface IMessageService
    {
        public Task<GenericResponseDTO<List<RecentChatDTO>>> GetRecentChats();
        public Task<GenericResponseDTO<List<MessageGetBeweenUserDTO>>> GetMessagesBetweenUsers(int receiverId);
        public Task<GenericResponseDTO<MessageDTO>> CreateMessage(CreateMessageDTO dto);
        public Task<GenericResponseDTO<bool>> RevokeMessage(int messageId);
    }
}
