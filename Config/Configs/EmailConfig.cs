namespace Valour.Config.Configs;

/*  Valour (TM) - A free and secure chat client
 *  Copyright (C) 2025 Valour Software LLC
 *  This program is subject to the GNU Affero General Public license
 *  A copy of the license should be included - if not, see <http://www.gnu.org/licenses/>
 */

public class EmailConfig
{
    /// <summary>
    /// The current Email Configuration
    /// </summary>
    public static EmailConfig Instance { get; private set; }

    /// <summary>
    /// The API key for the email service
    /// </summary>
    public string ApiKey { get; set; }

    /// <summary>
    /// HMAC-SHA256 secret for signing unsubscribe tokens
    /// </summary>
    public string UnsubscribeSecret { get; set; }

    /// <summary>
    /// CAN-SPAM physical mailing address
    /// </summary>
    public string PhysicalAddress { get; set; } = "99 Wall Street Suite 1299, New York, NY";

    /// <summary>
    /// Set instance to newest config
    /// </summary>
    public EmailConfig()
    {
        Instance = this;
    }
}
