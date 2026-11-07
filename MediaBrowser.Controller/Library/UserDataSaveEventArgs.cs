#nullable disable

#pragma warning disable CA1002, CA2227, CS1591

using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Library
{
    /// <summary>
    /// Class UserDataSaveEventArgs.
    /// </summary>
    public class UserDataSaveEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the user id.
        /// </summary>
        /// <value>The user id.</value>
        public Guid UserId { get; set; }

        public List<string> Keys { get; set; }

        /// <summary>
        /// Gets or sets the save reason.
        /// </summary>
        /// <value>The save reason.</value>
        public UserDataSaveReason SaveReason { get; set; }

        /// <summary>
        /// Gets or sets the user data.
        /// </summary>
        /// <value>The user data.</value>
        public UserItemData UserData { get; set; }

        /// <summary>
        /// Gets or sets the item.
        /// </summary>
        /// <value>The item.</value>
        public BaseItem Item { get; set; }

        /// <summary>
        /// Gets or sets whether the item was played before this save. Fork: set only for API updates
        /// (<see cref="UserDataSaveReason.UpdateUserData"/> and other dto saves); null otherwise.
        /// </summary>
        /// <value>Whether the item was played before the save.</value>
        public bool? PreviouslyPlayed { get; set; }
    }
}
