﻿using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using WebDating.DTOs;
using WebDating.Interfaces;
using WebDating.Extensions;
using WebDating.Entities;
using WebDating.Entities.MessageEntities;

namespace WebDating.SignalR
{
    [Authorize]
    public class MessageHub : Hub
    {
        private readonly IUnitOfWork _uow;
        private readonly IMapper _mapper;
        private readonly IHubContext<PresenceHub> _presenceHub;

        public MessageHub(IUnitOfWork uow, IMapper mapper, IHubContext<PresenceHub> presenceHub)
        {
            _uow = uow;
            _mapper = mapper;
            _presenceHub = presenceHub;
        }

        public override async Task OnConnectedAsync()
        {
            var httpContext = Context.GetHttpContext();
            var otherUser = httpContext.Request.Query["user"]; //message?user=   --messageservice
            var groupName = GetGroupName(Context.User.GetUserName(), otherUser);
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            var group = await AddToGroup(groupName);

            //Update group để client biết ai đang ở trong group tại bất kỳ thời điểm nào
            await Clients.Group(groupName).SendAsync("UpdatedGroup", group);

            var messages = await _uow.MessageRepository
                .GetMessageThread(Context.User.GetUserName(), otherUser);

            if (_uow.HasChanges()) await _uow.Complete();

            await Clients.Caller.SendAsync("ReceiveMessageThread", messages);
        }


        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var group = await RemoveFromMessageGroup();
            await Clients.Group(group.Name).SendAsync("UpdatedGroup");
            await base.OnDisconnectedAsync(exception);
        }



        public async Task SendMessage(CreateMessageDto createMessageDto)
        {

            var username = Context.User.GetUserName();

            if (username == createMessageDto.RecipientUsername.ToLower())
                throw new HubException("You cannot send message to yourself");

            var sender = await _uow.UserRepository.GetUserByUsernameAsync(username);
            var recipient = await _uow.UserRepository.GetUserByUsernameAsync(createMessageDto.RecipientUsername);

            if (recipient == null) throw new HubException("Not found user!");

            var message = new Message
            {
                Sender = sender,
                Recipient = recipient,
                SenderUsername = sender.UserName,
                RecipientUsername = recipient.UserName,
                Content = createMessageDto.Content,
                PublicId = createMessageDto.PublicId,
                Url = createMessageDto.Url
            };

            var groupName = GetGroupName(sender.UserName, recipient.UserName);

            var group = await _uow.MessageRepository.GetMessageGroup(groupName);

            //Nếu ng dùng ở trong group tin nhắn thì sẽ cập nhật time
            //else ng nhận tin nhắn online nhưng không ở trong group tin nhắn thì sẽ nhận đc thông báo có tin nhắn mới 
            if (group.Connections.Any(x => x.Username == recipient.UserName))
            {
                message.DateRead = DateTime.UtcNow;
            }
            else if (!group.Connections.Any(x => x.Username == recipient.UserName && message.DateRead != null))
            {
                var connections = await PresenceTracker.GetConnectionsForUser(recipient.UserName);
                if (connections != null)
                {
                    await _presenceHub.Clients.Clients(connections).SendAsync("NewMessageReceived",
                        new { username = sender.UserName, knownAs = sender.KnownAs }
                    );
                    await _presenceHub.Clients.Clients(connections).SendAsync("NewUnreadMessage", new { username = sender.UserName });
                }
            }

            _uow.MessageRepository.AddMessage(message);

            if (await _uow.Complete())
            {
                await Clients.Group(groupName).SendAsync("NewMessage", _mapper.Map<MessageDto>(message));
                var messageSummaryDto = new MessageSummaryDto
                {
                    Username = message.SenderUsername == username
                     ? message.RecipientUsername
                     : message.SenderUsername,
                            LastMessageContent = message.Content,
                            LastMessageSent = message.MessageSent,
                            Url = message.SenderUsername == username
                     ? message.Recipient.Photos.FirstOrDefault(p => p.IsMain)?.Url
                     : message.Sender.Photos.FirstOrDefault(p => p.IsMain)?.Url,
                            KnownAs = message.SenderUsername == username
                     ? message.Recipient.KnownAs
                     : message.Sender.KnownAs,
                    DateRead = message.RecipientUsername == username && message.DateRead == null
                };
                await Clients.Caller.SendAsync("UpdateUI", messageSummaryDto);
            }
        }



        //Nhóm group tên theo thứ tự từ điển
        private string GetGroupName(string caller, string other)
        {
            var stringCompare = string.CompareOrdinal(caller, other) < 0;
            return stringCompare ? $"{caller}-{other}" : $"{other}-{caller}";
        }

        private async Task<Group> AddToGroup(string groupName)
        {
            var group = await _uow.MessageRepository.GetMessageGroup(groupName);
            var connection = new Connection(Context.ConnectionId, Context.User.GetUserName());

            if (group == null)
            {
                group = new Group(groupName);
                _uow.MessageRepository.AddGroup(group);
            }

            group.Connections.Add(connection);

            if (await _uow.Complete()) return group;

            throw new HubException("Failed to add to group");

        }
        //Xóa kết nối khỏi database
        private async Task<Group> RemoveFromMessageGroup()
        {
            var group = await _uow.MessageRepository.GetGroupForConnection(Context.ConnectionId);
            var connection = group.Connections.FirstOrDefault(x => x.ConnectionId == Context.ConnectionId);
            _uow.MessageRepository.RemoveConnection(connection);

            if (await _uow.Complete()) return group;

            throw new HubException("Failed to remove from group");
        }

    }
}
