﻿#pragma warning disable 1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sanakan.Api.Models;
using Sanakan.Config;
using Sanakan.Database.Models;
using Sanakan.Extensions;
using Sanakan.Services.Executor;
using Sanakan.Services.PocketWaifu;
using Shinden;
using Z.EntityFramework.Plus;

namespace Sanakan.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WaifuController : ControllerBase
    {
        private readonly Waifu _waifu;
        private readonly IConfig _config;
        private readonly IExecutor _executor;
        private readonly ShindenClient _shClient;

        public WaifuController(ShindenClient shClient, Waifu waifu, IExecutor executor, IConfig config)
        {
            _waifu = waifu;
            _config = config;
            _executor = executor;
            _shClient = shClient;
        }

        /// <summary>
        /// Pobiera użytkowników posiadających karte postaci
        /// </summary>
        /// <param name="id">id postaci z bazy shindena</param>
        /// <returns>lista id</returns>
        /// <response code="404">Users not found</response>
        [HttpGet("users/owning/character/{id}"), Authorize(Policy = "Site")]
        public async Task<IEnumerable<ulong>> GetUsersOwningCharacterCardAsync(ulong id)
        {
            using (var db = new Database.UserContext(_config))
            {
                var shindenIds = await db.Cards.Include(x => x.GameDeck).ThenInclude(x => x.User)
                    .Where(x => x.Character == id && x.GameDeck.User.Shinden != 0).AsNoTracking().Select(x => x.GameDeck.User.Shinden).Distinct().ToListAsync();

                if (shindenIds.Count > 0)
                    return shindenIds;

                await "Users not found".ToResponse(404).ExecuteResultAsync(ControllerContext);
                return null;
            }
        }

        /// <summary>
        /// Pobiera liste kart użytkownika
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <returns>lista kart</returns>
        /// <response code="404">User not found</response>
        [HttpGet("user/{id}/cards"), Authorize(Policy = "Site")]
        public async Task<IEnumerable<Database.Models.Card>> GetUserCardsAsync(ulong id)
        {
            using (var db = new Database.UserContext(_config))
            {
                var user = await db.Users.Where(x => x.Shinden == id).Include(x => x.GameDeck).ThenInclude(x => x.Cards).ThenInclude(x => x.ArenaStats).Include(x => x.GameDeck)
                    .ThenInclude(x => x.Cards).ThenInclude(x => x.TagList).AsNoTracking().FirstOrDefaultAsync();

                if (user == null)
                {
                    await "User not found".ToResponse(404).ExecuteResultAsync(ControllerContext);
                    return new List<Database.Models.Card>();
                }

                return user.GameDeck.Cards;
            }
        }

        /// <summary>
        /// Pobiera profil użytkownika
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <returns>profil</returns>
        /// <response code="404">User not found</response>
        [HttpGet("user/{id}/profile"), Authorize(Policy = "Site")]
        public async Task<UserSiteProfile> GetUserWaifuProfileAsync(ulong id)
        {
            using (var db = new Database.UserContext(_config))
            {
                var user = await db.Users.Where(x => x.Shinden == id).Include(x => x.GameDeck).ThenInclude(x => x.Cards).ThenInclude(x => x.ArenaStats).Include(x => x.GameDeck)
                    .ThenInclude(x => x.Cards).ThenInclude(x => x.TagList).AsNoTracking().FirstOrDefaultAsync();

                if (user == null)
                {
                    await "User not found".ToResponse(404).ExecuteResultAsync(ControllerContext);
                    return new UserSiteProfile();
                }

                return new UserSiteProfile()
                {
                    SSSCount = user.GameDeck.Cards.Count(x => x.Rarity == Rarity.SSS),
                    SSCount = user.GameDeck.Cards.Count(x => x.Rarity == Rarity.SS),
                    SCount = user.GameDeck.Cards.Count(x => x.Rarity == Rarity.S),
                    ACount = user.GameDeck.Cards.Count(x => x.Rarity == Rarity.A),
                    BCount = user.GameDeck.Cards.Count(x => x.Rarity == Rarity.B),
                    CCount = user.GameDeck.Cards.Count(x => x.Rarity == Rarity.C),
                    DCount = user.GameDeck.Cards.Count(x => x.Rarity == Rarity.D),
                    ECount = user.GameDeck.Cards.Count(x => x.Rarity == Rarity.E),
                    Gallery = user.GameDeck.Cards.Where(x => x.HasTag("galeria")).ToList(),
                    Waifu = user.GameDeck.Cards.Where(x => x.Character == user.GameDeck.Waifu).OrderBy(x => x.Rarity).FirstOrDefault()
                };
            }
        }

        /// <summary>
        /// Zastępuje id postaci w kartach
        /// </summary>
        /// <param name="oldId">id postaci z bazy shindena, która została usunięta</param>
        /// <param name="newId">id nowej postaci z bazy shindena</param>
        /// <response code="500">New character ID is invalid!</response>
        [HttpPost("character/repair/{oldId}/{newId}"), Authorize(Policy = "Site")]
        public async Task RepairCardsAsync(ulong oldId, ulong newId)
        {
            var response = await _shClient.GetCharacterInfoAsync(newId);
            if (!response.IsSuccessStatusCode())
            {
                await "New character ID is invalid!".ToResponse(500).ExecuteResultAsync(ControllerContext);
                return;
            }

            var exe = new Executable($"api-repair oc{oldId} c{newId}", new Task(() =>
            {
                using (var db = new Database.UserContext(_config))
                {
                    var userRelease = new List<string>() { "users" };
                    var cards = db.Cards.Where(x => x.Character == oldId);

                    foreach (var card in cards)
                    {
                        card.Character = newId;
                        userRelease.Add($"user-{card.GameDeckId}");
                    }

                    db.SaveChanges();

                    QueryCacheManager.ExpireTag(userRelease.ToArray());
                }
            }));

            await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));
            await "Success".ToResponse(200).ExecuteResultAsync(ControllerContext);
        }

        /// <summary>
        /// Podmienia dane na karcie danej postaci
        /// </summary>
        /// <param name="id">id postaci z bazy shindena</param>
        /// <param name="newData">nowe dane karty</param>
        [HttpPost("cards/character/{id}/update"), Authorize(Policy = "Site")]
        public async Task UpdateCardInfoAsync(ulong id, [FromBody]Models.CharacterCardInfoUpdate newData)
        {
            var exe = new Executable($"update cards-{id} img", new Task(() =>
            {
                using (var db = new Database.UserContext(_config))
                {
                    var userRelease = new List<string>() { "users" };
                    var cards = db.Cards.Where(x => x.Character == id);

                    foreach (var card in cards)
                    {
                        if (newData?.ImageUrl != null)
                            card.Image = newData.ImageUrl;

                        if (newData?.CharacterName != null)
                            card.Name = newData.CharacterName;

                        if (newData?.CardSeriesTitle != null)
                            card.Title = newData.CardSeriesTitle;

                        try
                        {
                            _waifu.DeleteCardImageIfExist(card);
                            _ = _waifu.GenerateAndSaveCardAsync(card).Result;
                        }
                        catch (Exception) { }

                        userRelease.Add($"user-{card.GameDeckId}");
                    }

                    db.SaveChanges();

                    QueryCacheManager.ExpireTag(userRelease.ToArray());
                }
            }));

            await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));
            await "Started!".ToResponse(200).ExecuteResultAsync(ControllerContext);
        }

        /// <summary>
        /// Generuje na nowo karty danej postaci
        /// </summary>
        /// <param name="id">id postaci z bazy shindena</param>
        /// <response code="404">Character not found</response>
        /// <response code="405">Image in character date not found</response>
        [HttpPost("users/make/character/{id}"), Authorize(Policy = "Site")]
        public async Task GenerateCharacterCardAsync(ulong id)
        {
            var response = await _shClient.GetCharacterInfoAsync(id);
            if (!response.IsSuccessStatusCode())
            {
                await "Character not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                return;
            }

            if (!response.Body.HasImage)
            {
                await "There is no character image!".ToResponse(405).ExecuteResultAsync(ControllerContext);
                return;
            }

            var exe = new Executable($"update cards-{id}", new Task(() =>
                {
                    using (var db = new Database.UserContext(_config))
                    {
                        var userRelease = new List<string>() { "users" };
                        var cards = db.Cards.Where(x => x.Character == id);

                        foreach (var card in cards)
                        {
                            card.Image = response.Body.PictureUrl;

                            try
                            {
                                _waifu.DeleteCardImageIfExist(card);
                                _ = _waifu.GenerateAndSaveCardAsync(card).Result;
                            }
                            catch (Exception) { }

                            userRelease.Add($"user-{card.GameDeckId}");
                        }

                        db.SaveChanges();

                        QueryCacheManager.ExpireTag(userRelease.ToArray());
                    }
                }));

                await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));
                await "Started!".ToResponse(200).ExecuteResultAsync(ControllerContext);
        }

        /// <summary>
        /// Pobiera listę życzeń użytkownika
        /// </summary>
        /// <param name="id">id użytkownika discorda</param>
        /// <response code="404">User not found</response>
        [HttpGet("user/discord/{id}/wishlist"), Authorize(Policy = "Site")]
        public async Task<IEnumerable<Database.Models.Card>> GetUserWishlistAsync(ulong id)
        {
            using (var db = new Database.UserContext(_config))
            {
                var user = await db.GetCachedFullUserAsync(id);
                if (user == null)
                {
                    await "User not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                    return null;
                }

                if (user.GameDeck.Wishes.Count < 1)
                {
                    await "Wishlist not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                    return null;
                }

                var p = user.GameDeck.GetCharactersWishList();
                var t = user.GameDeck.GetTitlesWishList();
                var c = user.GameDeck.GetCardsWishList();

                return await _waifu.GetCardsFromWishlist(c, p ,t, db, user.GameDeck.Cards);
            }
        }

        /// <summary>
        /// Pobiera listę życzeń użytkownika
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <response code="404">User not found</response>
        [HttpGet("user/shinden/{id}/wishlist"), Authorize(Policy = "Site")]
        public async Task<IEnumerable<Database.Models.Card>> GetShindenUserWishlistAsync(ulong id)
        {
            using (var db = new Database.UserContext(_config))
            {
                var user = await db.GetCachedFullUserByShindenIdAsync(id);
                if (user == null)
                {
                    await "User not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                    return null;
                }

                if (user.GameDeck.Wishes.Count < 1)
                {
                    await "Wishlist not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                    return null;
                }

                var p = user.GameDeck.GetCharactersWishList();
                var t = user.GameDeck.GetTitlesWishList();
                var c = user.GameDeck.GetCardsWishList();

                return await _waifu.GetCardsFromWishlist(c, p ,t, db, user.GameDeck.Cards);
            }
        }

        /// <summary>
        /// Pobiera liste kart z danym tagiem
        /// </summary>
        /// <param name="tag">tag na karcie</param>
        [HttpGet("cards/tag/{tag}"), Authorize(Policy = "Site")]
        public async Task<IEnumerable<Database.Models.Card>> GetCardsWithTagAsync(string tag)
        {
            using (var db = new Database.UserContext(_config))
            {
                return await db.Cards.Include(x => x.ArenaStats).Include(x => x.TagList).Where(x => x.TagList.Any(c => c.Name.Equals(tag, StringComparison.CurrentCultureIgnoreCase))).AsNoTracking().ToListAsync();
            }
        }

        /// <summary>
        /// Wymusza na bocie wygenerowanie obrazka jeśli nie istnieje
        /// </summary>
        /// <param name="id">id karty (wid)</param>
        /// <response code="403">Card already exist</response>
        /// <response code="404">Card not found</response>
        [HttpGet("card/{id}")]
        public async Task GetCardAsync(ulong id)
        {
            if (!System.IO.File.Exists($"./GOut/Cards/Small/{id}.png") || !System.IO.File.Exists($"./GOut/Cards/{id}.png"))
            {
                using (var db = new Database.UserContext(_config))
                {
                    var card = await db.Cards.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                    if (card == null)
                    {
                        await "Card not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                        return;
                    }

                    var cardImage = await _waifu.GenerateAndSaveCardAsync(card);
                    await ControllerContext.HttpContext.Response.SendFileAsync(cardImage);
                }
            }
            else
            {
                await "Card already exist!".ToResponse(403).ExecuteResultAsync(ControllerContext);
            }
        }

        /// <summary>
        /// Daje użytkownikowi pakiety kart
        /// </summary>
        /// <param name="id">id użytkownika discorda</param>
        /// <param name="boosterPacks">model pakietu</param>
        /// <returns>użytkownik bota</returns>
        /// <response code="404">User not found</response>
        /// <response code="500">Model is Invalid</response>
        [HttpPost("discord/{id}/boosterpack"), Authorize(Policy = "Site")]
        public async Task GiveUserAPacksAsync(ulong id, [FromBody]List<Models.CardBoosterPack> boosterPacks)
        {
            if (boosterPacks?.Count < 1)
            {
                await "Model is Invalid".ToResponse(500).ExecuteResultAsync(ControllerContext);
                return;
            }

            var packs = new List<BoosterPack>();
            foreach (var pack in boosterPacks)
            {
                var rPack = pack.ToRealPack();
                if (rPack != null) packs.Add(rPack);
            }

            if (packs.Count < 1)
            {
                await "Data is Invalid".ToResponse(500).ExecuteResultAsync(ControllerContext);
                return;
            }

            using (var db = new Database.UserContext(_config))
            {
                var user = await db.GetCachedFullUserAsync(id);
                if (user == null)
                {
                    await "User not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                    return;
                }

                var exe = new Executable($"api-packet u{id}", new Task(() =>
                {
                    using (var dbs = new Database.UserContext(_config))
                    {
                        var botUser = dbs.GetUserOrCreateAsync(id).Result;

                        foreach (var pack in packs)
                            botUser.GameDeck.BoosterPacks.Add(pack);

                        dbs.SaveChanges();

                        QueryCacheManager.ExpireTag(new string[] { $"user-{botUser.Id}", "users" });
                    }
                }));

                await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));
                await "Boosterpack added!".ToResponse(200).ExecuteResultAsync(ControllerContext);
            }
        }

        /// <summary>
        /// Daje użytkownikowi pakiety kart
        /// </summary>
        /// <param name="id">id użytkownika shindena</param>
        /// <param name="boosterPacks">model pakietu</param>
        /// <returns>użytkownik bota</returns>
        /// <response code="404">User not found</response>
        /// <response code="500">Model is Invalid</response>
        [HttpPost("shinden/{id}/boosterpack"), Authorize(Policy = "Site")]
        public async Task<UserWithToken> GiveShindenUserAPacksAsync(ulong id, [FromBody]List<Models.CardBoosterPack> boosterPacks)
        {
            if (boosterPacks?.Count < 1)
            {
                await "Model is Invalid".ToResponse(500).ExecuteResultAsync(ControllerContext);
                return null;
            }

            var packs = new List<BoosterPack>();
            foreach (var pack in boosterPacks)
            {
                var rPack = pack.ToRealPack();
                if (rPack != null) packs.Add(rPack);
            }

            if (packs.Count < 1)
            {
                await "Data is Invalid".ToResponse(500).ExecuteResultAsync(ControllerContext);
                return null;
            }

            using (var db = new Database.UserContext(_config))
            {
                var user = await db.GetCachedFullUserByShindenIdAsync(id);
                if (user == null)
                {
                    await "User not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                    return null;
                }

                var discordId = user.Id;
                var exe = new Executable($"api-packet u{discordId}", new Task(() =>
                {
                    using (var dbs = new Database.UserContext(_config))
                    {
                        var botUser = dbs.GetUserOrCreateAsync(discordId).Result;

                        foreach (var pack in packs)
                            botUser.GameDeck.BoosterPacks.Add(pack);

                        dbs.SaveChanges();

                        QueryCacheManager.ExpireTag(new string[] { $"user-{botUser.Id}", "users" });
                    }
                }));

                await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));

                TokenData tokenData = null;
                var currUser = ControllerContext.HttpContext.User;
                if (currUser.HasClaim(x => x.Type == ClaimTypes.Webpage))
                {
                    tokenData = UserTokenBuilder.BuildUserToken(_config, user);
                }

                return new UserWithToken()
                {
                    Expire = tokenData?.Expire,
                    Token = tokenData?.Token,
                    User = user,
                };
            }
        }

        /// <summary>
        /// Otwiera pakiet użytkownika (wymagany Bearer od użytkownika)
        /// </summary>
        /// <param name="packNumber">numer pakietu</param>
        /// <response code="403">The appropriate claim was not found</response>
        /// <response code="404">User not found</response>
        [HttpPost("boosterpack/open/{packNumber}"), Authorize(Policy = "Player")]
        public async Task<List<Card>> OpenAPackAsync(int packNumber)
        {
            var currUser = ControllerContext.HttpContext.User;
            if (currUser.HasClaim(x => x.Type == "DiscordId"))
            {
                if (ulong.TryParse(currUser.Claims.First(x => x.Type == "DiscordId").Value, out var discordId))
                {
                    var cards = new List<Card>();
                    using (var db = new Database.UserContext(_config))
                    {
                        var botUserCh = await db.GetCachedFullUserAsync(discordId);
                        if (botUserCh == null)
                        {
                            await "User not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                            return null;
                        }

                        if (botUserCh.GameDeck.BoosterPacks.Count < packNumber || packNumber <= 0)
                        {
                            await "Boosterpack not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                            return null;
                        }

                        var pack = botUserCh.GameDeck.BoosterPacks.ToArray()[packNumber - 1];
                        cards = await _waifu.OpenBoosterPackAsync(null, pack);
                    }

                    var exe = new Executable($"api-packet-open u{discordId}", new Task(async () =>
                    {
                        using (var db = new Database.UserContext(_config))
                        {
                            var botUser = await db.GetUserOrCreateAsync(discordId);

                            var bPack = botUser.GameDeck.BoosterPacks.ToArray()[packNumber - 1];
                            botUser.GameDeck.BoosterPacks.Remove(bPack);

                            if (bPack.CardSourceFromPack == CardSource.Activity || bPack.CardSourceFromPack == CardSource.Migration)
                            {
                                botUser.Stats.OpenedBoosterPacksActivity += 1;
                            }
                            else
                            {
                                botUser.Stats.OpenedBoosterPacks += 1;
                            }

                            foreach (var card in cards)
                            {
                                card.Affection += botUser.GameDeck.AffectionFromKarma();
                                card.FirstIdOwner = botUser.Id;

                                botUser.GameDeck.Cards.Add(card);
                                botUser.GameDeck.RemoveCharacterFromWishList(card.Character);
                            }

                            await db.SaveChangesAsync();

                            QueryCacheManager.ExpireTag(new string[] { $"user-{botUser.Id}", "users" });
                        }
                    }));

                    await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));

                    exe.Wait();
                    await Task.Delay(2000);

                    return cards;
                }
            }
            await "The appropriate claim was not found".ToResponse(403).ExecuteResultAsync(ControllerContext);
            return null;
        }

        /// <summary>
        /// Aktywuje lub dezaktywuje kartę (wymagany Bearer od użytkownika)
        /// </summary>
        /// <param name="wid">id karty</param>
        /// <response code="403">The appropriate claim was not found</response>
        /// <response code="404">Card not found</response>
        [HttpPut("deck/toggle/card/{wid}"), Authorize(Policy = "Player")]
        public async Task ToggleCardStatusAsync(ulong wid)
        {
            var currUser = ControllerContext.HttpContext.User;
            if (currUser.HasClaim(x => x.Type == "DiscordId"))
            {
                if (ulong.TryParse(currUser.Claims.First(x => x.Type == "DiscordId").Value, out var discordId))
                {
                    using (var db = new Database.UserContext(_config))
                    {
                        var botUserCh = await db.GetCachedFullUserAsync(discordId);
                        if (botUserCh == null)
                        {
                            await "User not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                            return;
                        }

                        var thisCardCh = botUserCh.GameDeck.Cards.FirstOrDefault(x => x.Id == wid);
                        if (thisCardCh == null)
                        {
                            await "Card not found!".ToResponse(404).ExecuteResultAsync(ControllerContext);
                            return;
                        }

                        if (thisCardCh.InCage)
                        {
                            await "Card is in cage!".ToResponse(403).ExecuteResultAsync(ControllerContext);
                            return;
                        }
                    }

                    var exe = new Executable($"api-deck u{discordId}", new Task(() =>
                    {
                        using (var db = new Database.UserContext(_config))
                        {
                            var botUser = db.GetUserOrCreateAsync(discordId).Result;
                            var thisCard = botUser.GameDeck.Cards.FirstOrDefault(x => x.Id == wid);
                            thisCard.Active = !thisCard.Active;

                            db.SaveChanges();

                            QueryCacheManager.ExpireTag(new string[] { $"user-{botUser.Id}", "users" });
                        }
                    }));

                    await _executor.TryAdd(exe, TimeSpan.FromSeconds(1));
                    await "Card status toggled".ToResponse(200).ExecuteResultAsync(ControllerContext);
                    return;
                }
            }
            await "The appropriate claim was not found".ToResponse(403).ExecuteResultAsync(ControllerContext);
        }
    }
}