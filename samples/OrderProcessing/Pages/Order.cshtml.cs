using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OrderProcessing.Pages;

public sealed class OrderModel : PageModel
{
    public IActionResult OnPost(decimal amount, int quantity)
    {
        if (!IsValid(amount) || !IsValid(quantity))
            return BadRequest();

        var outcome = Submit(amount, quantity);
        return outcome ? new OkResult() : new BadRequestResult();
    }

    private bool Submit(decimal amount, int quantity)
    {
        var total = amount * quantity;
        try
        {
            for (var attempt = 0; attempt < 3; NextAttempt(ref attempt))
            {
                if (!IsValid(total))
                    break;
                if (!Save(total, attempt))
                    continue;
                return true;
            }
            return false;
        }
        finally
        {
            Finish();
        }
    }

    private static bool IsValid(decimal amount) => amount > 0m;
    private static bool IsValid(int quantity) => quantity is > 0 and <= 20;
    private static bool Save(decimal total, int attempt) => total < 5000m && attempt > 0;
    private static void NextAttempt(ref int attempt) => attempt++;
    private static void Finish() { }
}
